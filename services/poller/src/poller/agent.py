"""The cycle: fetch what to do, say you are alive, sleep, repeat."""

from __future__ import annotations

import asyncio
import logging
import uuid
from datetime import UTC, datetime
from typing import Any, Protocol

from .api import ConfigVersionRejectedError, PollerNotRegisteredError
from .bus import HEARTBEAT_MESSAGE_URN, Publisher, build_envelope
from .config import ConfigState
from .settings import Settings

logger = logging.getLogger("poller.agent")


class ApiClient(Protocol):
    """The API surface the cycle uses, narrowed so tests need no HTTP."""

    async def register(self) -> dict[str, Any]: ...

    async def fetch_config(self, since_version: int | None) -> dict[str, Any]: ...


class PollerAgent:
    """
    One cycle does three things and survives all of them failing.

    Nothing here raises out of `run_cycle`: a poller that dies because the API was restarting is a
    poller an operator has to go and start again, and the platform already notices silence through
    the heartbeat. The same rule that says one dead device must not abort a polling cycle applies to
    the poller's own dependencies.
    """

    def __init__(
        self,
        settings: Settings,
        api: ApiClient,
        publisher: Publisher,
        state: ConfigState | None = None,
    ) -> None:
        self._settings = settings
        self._api = api
        self._publisher = publisher
        self._state = state if state is not None else ConfigState()
        self._registered = False
        self._cycle_number = 0

    @property
    def state(self) -> ConfigState:
        return self._state

    @property
    def cycle_number(self) -> int:
        return self._cycle_number

    async def run_cycle(self) -> None:
        self._cycle_number += 1
        await self._ensure_registered()
        await self._refresh_config()
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
