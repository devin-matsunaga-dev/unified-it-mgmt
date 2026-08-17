"""The cycle: fetch what to do, say you are alive, sleep, repeat."""

from __future__ import annotations

import asyncio
import logging
import uuid
from collections.abc import Sequence
from datetime import UTC, datetime
from typing import Any, Protocol

from .api import ConfigVersionRejectedError, PollerNotRegisteredError
from .bus import HEARTBEAT_MESSAGE_URN, NullPublisher, Publisher, build_envelope
from .config import ConfigState
from .polling import CheckResult, PollingEngine, ReachabilityChange
from .runbooks import RunbookRequest, RunbookRunner
from .scheduler import CheckScheduler
from .settings import Settings
from .telemetry import (
    REACHABILITY_MESSAGE_URN,
    TELEMETRY_MESSAGE_URN,
    build_reachability,
    build_telemetry,
)
from .vault import CredentialStore, parse_released, parse_scope

logger = logging.getLogger("poller.agent")


class ApiClient(Protocol):
    """The API surface the cycle uses, narrowed so tests need no HTTP."""

    async def register(self) -> dict[str, Any]: ...

    async def fetch_config(self, since_version: int | None) -> dict[str, Any]: ...

    async def fetch_credential_scope(self) -> dict[str, Any]: ...

    async def request_credential_grant(self) -> dict[str, Any]: ...

    async def redeem_credential_grant(self, grant_id: str, token: str) -> dict[str, Any]: ...

    async def fetch_runbook_executions(self) -> dict[str, Any]: ...

    async def report_runbook_result(
        self, execution_id: str, result: dict[str, Any]
    ) -> dict[str, Any] | None: ...


class PollerAgent:
    """
    One cycle registers, refreshes its configuration, polls what is due, and says it is alive — and
    survives all four failing.

    Nothing here raises out of `run_cycle`: a poller that dies because the API was restarting is a
    poller an operator has to go and start again, and the platform already notices silence through
    the heartbeat. The same rule that says one dead device must not abort a polling cycle applies to
    the poller's own dependencies.

    The heartbeat is published last and unconditionally. It is the poller saying it completed a
    cycle, and a cycle in which every device was unreachable is one it completed.
    """

    def __init__(
        self,
        settings: Settings,
        api: ApiClient,
        publisher: Publisher,
        state: ConfigState | None = None,
        engine: PollingEngine | None = None,
        scheduler: CheckScheduler | None = None,
        telemetry_publisher: Publisher | None = None,
        reachability_publisher: Publisher | None = None,
        credentials: CredentialStore | None = None,
        runbooks: RunbookRunner | None = None,
    ) -> None:
        self._settings = settings
        self._api = api
        self._publisher = publisher
        self._state = state if state is not None else ConfigState()
        # No engine means a poller that registers, fetches and heartbeats without polling anything.
        # That is what this agent was before WP-3.3, and it is what the config-and-heartbeat tests
        # exercise; `__main__` always wires a real one.
        self._engine = engine
        self._scheduler = scheduler if scheduler is not None else CheckScheduler()
        self._telemetry = telemetry_publisher or NullPublisher()
        self._reachability = reachability_publisher or NullPublisher()
        self._credentials = credentials if credentials is not None else CredentialStore()
        # No runner means a poller that never remediates. That is what every test written before
        # WP-5.6 exercises, and `__main__` always wires a real one.
        self._runbooks = runbooks
        self._registered = False
        self._cycle_number = 0

    @property
    def state(self) -> ConfigState:
        return self._state

    @property
    def credentials(self) -> CredentialStore:
        return self._credentials

    @property
    def cycle_number(self) -> int:
        return self._cycle_number

    async def run_cycle(self) -> None:
        self._cycle_number += 1
        await self._ensure_registered()
        await self._refresh_config()
        await self._refresh_credentials()
        await self._poll_devices()
        await self._run_runbooks()
        await self._publish_heartbeat()

    async def run_forever(self, stop: asyncio.Event | None = None) -> None:
        """Runs until cancelled, or until `stop` is set between cycles."""
        while stop is None or not stop.is_set():
            await self.run_cycle()
            if stop is None:
                await asyncio.sleep(self._settings.interval_seconds)
                continue
            try:
                await asyncio.wait_for(stop.wait(), timeout=self._settings.interval_seconds)
            except TimeoutError:
                continue

    async def _ensure_registered(self) -> None:
        if self._registered:
            return
        try:
            await self._api.register()
        except Exception:
            logger.exception("Registration failed; retrying next cycle.", extra=self._context())
            return
        self._registered = True
        logger.info("Registered.", extra=self._context())

    async def _refresh_config(self) -> None:
        try:
            await self._fetch_and_apply(self._state.version or None)
        except ConfigVersionRejectedError:
            # The version this poller holds is not one this server issued. Forget it and take the
            # full snapshot now rather than next cycle, so the poller is not blind for an interval.
            logger.warning(
                "The server rejected the held config version; taking a full snapshot.",
                extra=self._context(),
            )
            self._state.forget()
            # Everything the poller believed about these devices came from a history the server has
            # disowned, including which of them were up and when each check was last run.
            self._scheduler.forget()
            self._credentials.forget()
            if self._engine is not None:
                self._engine.forget()
            try:
                await self._fetch_and_apply(None)
            except Exception:
                logger.exception("Full config snapshot failed.", extra=self._context())
        except PollerNotRegisteredError:
            # Registration is upserted, so re-registering next cycle is the whole recovery.
            logger.warning("The platform does not know this poller; re-registering.",
                           extra=self._context())
            self._registered = False
        except Exception:
            # The previous configuration stays in force. A poller that cleared its devices because
            # one request timed out would stop monitoring an estate that is still there.
            logger.exception("Config fetch failed; keeping the configuration already held.",
                             extra=self._context())

    async def _fetch_and_apply(self, since_version: int | None) -> None:
        response = await self._api.fetch_config(since_version)
        applied = self._state.apply(response)
        logger.info(
            "Configuration applied.",
            extra=self._context()
            | {
                "config_version": applied.version,
                "full_snapshot": applied.full_snapshot,
                "devices_upserted": applied.upserted,
                "devices_removed": applied.removed,
                "device_count": applied.device_count,
            },
        )

    async def _refresh_credentials(self) -> None:
        """
        Fetches credential material, but only when there is a version this poller does not hold.

        Two calls, and the split is the whole design. The scope is metadata — ids and version
        numbers — so asking for it every cycle costs nothing and writes nothing; the grant is a row
        on the server and an audited release of secrets, so it is asked for only when the scope says
        something moved. Rotating a credential bumps its version, this notices on the next cycle,
        and the check authenticates with the new secret on the one after at the latest.

        Nothing here raises. A vault that cannot be reached leaves the previously held material in
        force — the same rule as a config fetch that fails — because a poller that dropped its
        credentials over one timed-out request would unauthenticate an estate that is still fine.
        """
        try:
            scope = parse_scope(await self._api.fetch_credential_scope())
        except PollerNotRegisteredError:
            # Handled by the config refresh, which runs first and has already asked to re-register.
            return
        except Exception:
            logger.exception(
                "Credential scope fetch failed; keeping the material already held.",
                extra=self._context(),
            )
            return

        # Before the early return, so a credential that leaves this poller's scope is dropped even
        # on a cycle where nothing needs fetching.
        self._credentials.retain({need.credential_id for need in scope})

        if not scope or not self._credentials.needs(scope):
            return

        try:
            grant = await self._api.request_credential_grant()
            grant_id = str(grant.get("grantId") or "")
            token = str(grant.get("token") or "")
            if not grant_id or not token:
                # An empty grant is the platform saying there is nothing to release. Not an error:
                # an estate where nothing authenticates is the normal state of a fresh install.
                return
            released = parse_released(await self._api.redeem_credential_grant(grant_id, token))
        except Exception:
            logger.exception(
                "Credential fetch failed; keeping the material already held.",
                extra=self._context())
            return

        stored = self._credentials.remember(released)
        # Ids and a count. Never a name-to-value mapping, and never the material — the point of
        # the vault is that the secret exists in this process and nowhere else it can be read from.
        logger.info(
            "Credentials refreshed.",
            extra=self._context() | {"credentials": len(stored), "credential_ids": stored},
        )

    async def _poll_devices(self) -> None:
        if self._engine is None:
            return

        try:
            self._engine.retain(set(self._state.devices))
            # Material is merged in here rather than held on the cached configuration, so a secret
            # lives in one place and a rotation reaches every check on the cycle after it lands.
            due = [self._credentials.apply(check)
                   for check in self._scheduler.due(self._state.devices.values())]
            outcome = await self._engine.run(due)
        except Exception:
            # The engine contains its own failures per device, so reaching here means the cycle's
            # own machinery broke rather than a target. Still not fatal: the next cycle re-reads the
            # configuration and tries again, and the heartbeat below still reports the poller alive.
            logger.exception("The polling cycle failed.", extra=self._context())
            return

        if not outcome.results:
            return

        logger.info(
            "Polled.",
            extra=self._context()
            | {
                "checks_run": len(outcome.results),
                "checks_failed": outcome.failed,
                "reachability_changes": len(outcome.changes),
            },
        )
        await self._publish_telemetry(outcome.results)
        for change in outcome.changes:
            await self._publish_reachability(change)

    async def _run_runbooks(self) -> None:
        """
        Collects whatever remediation the platform has waiting, runs it, and reports each result.

        After polling, not before: a cycle's first duty is to measure the estate, and a remediation
        that took its whole timeout would otherwise delay every reading behind it. Nothing here
        raises — a failed fetch means this cycle remediates nothing and the next one asks again,
        which is the same rule the configuration and credential refreshes follow.

        A result that cannot be reported is *not* retried by re-running the runbook. The platform's
        own deadline will finish the execution and escalate it, and running a service restart twice
        because an HTTP POST failed is precisely the retry storm the WP forbids.
        """
        if self._runbooks is None or not self._settings.runbooks_enabled:
            return

        try:
            payload = await self._api.fetch_runbook_executions()
        except PollerNotRegisteredError:
            # Handled by the config refresh, which runs first and has already asked to re-register.
            return
        except Exception:
            logger.exception(
                "Runbook fetch failed; nothing was remediated this cycle.",
                extra=self._context(),
            )
            return

        raw = payload.get("executions") or []
        if not raw:
            return

        logger.info(
            "Runbooks claimed.", extra=self._context() | {"runbooks": len(raw)})

        for entry in raw:
            try:
                request = RunbookRequest.parse(entry)
            except (KeyError, TypeError, ValueError):
                # A dispatch this agent cannot even read. Skipped rather than guessed at: the
                # platform's deadline finishes it and a human is told, which is the right outcome
                # for a server and an agent that disagree about the shape of an instruction.
                logger.exception(
                    "A dispatched runbook could not be read; it was left alone.",
                    extra=self._context(),
                )
                continue

            result = await self._runbooks.run(request)
            logger.info(
                "Runbook finished.",
                extra=self._context()
                | {
                    "execution": result.execution_id,
                    "runbook": request.key,
                    "outcome": result.outcome,
                    "exit_code": result.exit_code,
                },
            )
            try:
                await self._api.report_runbook_result(result.execution_id, result.as_payload())
            except Exception:
                logger.exception(
                    "A runbook result could not be reported; the platform will time it out.",
                    extra=self._context() | {"execution": result.execution_id},
                )

    async def _publish_telemetry(self, results: Sequence[CheckResult]) -> None:
        event_id = str(uuid.uuid4())
        payload = build_telemetry(
            results,
            poller_name=self._settings.name,
            poller_group=self._settings.poller_group,
            cycle_number=self._cycle_number,
            event_id=event_id,
        )
        await self._publish(
            self._telemetry, payload, TELEMETRY_MESSAGE_URN, event_id, "Telemetry")

    async def _publish_reachability(self, change: ReachabilityChange) -> None:
        event_id = str(uuid.uuid4())
        payload = build_reachability(
            change,
            poller_name=self._settings.name,
            poller_group=self._settings.poller_group,
            event_id=event_id,
        )
        await self._publish(
            self._reachability, payload, REACHABILITY_MESSAGE_URN, event_id, "Reachability")
        logger.info(
            "Device %s.", "came back" if change.is_reachable else "stopped answering",
            extra=self._context()
            | {
                "device": change.device_id,
                "address": change.address,
                "reachable": change.is_reachable,
                "consecutive_failures": change.consecutive_failures,
            },
        )

    async def _publish(
        self,
        publisher: Publisher,
        payload: dict[str, Any],
        message_urn: str,
        event_id: str,
        what: str,
    ) -> None:
        envelope = build_envelope(
            payload, message_urn, source=self._settings.name, message_id=event_id)
        try:
            await publisher.publish(envelope)
        except Exception:
            # A measurement that could not be published is lost, and deliberately so: the next cycle
            # measures again, and a poller that queued telemetry through a broker outage would come
            # back and publish an hour of readings all stamped as if they had just happened.
            logger.exception(f"{what} publish failed.", extra=self._context())

    async def _publish_heartbeat(self) -> None:
        event_id = str(uuid.uuid4())
        heartbeat: dict[str, Any] = {
            "eventId": event_id,
            "occurredAt": datetime.now(UTC).isoformat(),
            "pollerName": self._settings.name,
            "pollerGroup": self._settings.poller_group,
            "agentVersion": self._settings.agent_version,
            "configVersion": self._state.version,
            "intervalSeconds": self._settings.interval_seconds,
            "deviceCount": len(self._state.devices),
            "cycleNumber": self._cycle_number,
        }
        envelope = build_envelope(
            heartbeat,
            HEARTBEAT_MESSAGE_URN,
            source=self._settings.name,
            message_id=event_id,
        )
        try:
            await self._publisher.publish(envelope)
        except Exception:
            # A missed beat is exactly what the platform's missed-heartbeat rule is for; two in a
            # row are reported, and a poller that crashed on a broker hiccup would report itself
            # dead for a much longer outage than it had.
            logger.exception("Heartbeat publish failed.", extra=self._context())
            return
        logger.info(
            "Heartbeat published.",
            extra=self._context()
            | {
                "config_version": self._state.version,
                "device_count": len(self._state.devices),
            },
        )

    def _context(self) -> dict[str, Any]:
        return {
            "poller": self._settings.name,
            "poller_group": self._settings.poller_group,
            "cycle": self._cycle_number,
        }
