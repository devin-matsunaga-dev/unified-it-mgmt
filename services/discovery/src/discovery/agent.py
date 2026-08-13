"""The cycle: fetch what to scan, scan whatever is due, publish what was found."""

from __future__ import annotations

import asyncio
import logging
import uuid
from typing import Any, Protocol

from .bus import NullPublisher, Publisher, build_envelope
from .config import ConfigState, ScanProfile
from .events import DISCOVERED_MESSAGE_URN, build_discovered
from .scanner import ScanOutcome
from .scheduler import ScanScheduler
from .settings import Settings

logger = logging.getLogger("discovery.agent")


class ApiClient(Protocol):
    """The API surface the cycle uses, narrowed so tests need no HTTP."""

    async def fetch_scan_profiles(self) -> dict[str, Any]: ...


class ScanRunner(Protocol):
    """
    What the cycle asks of a scanner, narrowed so tests need no network.

    A protocol rather than :class:`Scanner` itself, for the reason `ApiClient` is one: the cycle's
    job is scheduling and publishing, and a test of that should not have to construct a sweep.
    """

    async def scan(self, profile: ScanProfile) -> ScanOutcome: ...


class DiscoveryAgent:
    """
    One cycle refreshes the profile list, scans what is due, and publishes each device it found,
    and survives all three failing.

    Nothing here raises out of `run_cycle`, the poller's rule applied to a scanner: a service that
    exited because the API was restarting is one somebody has to go and start again, and unlike a
    poller there is no heartbeat for the platform to miss — so a crash here is silent until
    somebody notices the review queue has stopped filling. That is a reason to be more careful, not
    less.

    There is deliberately no heartbeat and no registration. A scan's evidence is the discoveries it
    publishes, and a scanner with nothing new to report is indistinguishable from an estate with
    nothing new on it — which is the normal state of a scanned network and not a fault worth
    modelling. WP-4.2's review queue is what somebody actually watches.
    """

    def __init__(
        self,
        settings: Settings,
        api: ApiClient,
        scanner: ScanRunner,
        publisher: Publisher | None = None,
        state: ConfigState | None = None,
        scheduler: ScanScheduler | None = None,
    ) -> None:
        self._settings = settings
        self._api = api
        self._scanner = scanner
        self._publisher = publisher or NullPublisher()
        self._state = state if state is not None else ConfigState()
        self._scheduler = scheduler if scheduler is not None else ScanScheduler()
        self._cycle_number = 0

    @property
    def state(self) -> ConfigState:
        return self._state

    @property
    def cycle_number(self) -> int:
        return self._cycle_number

    async def run_cycle(self) -> None:
        self._cycle_number += 1
        await self._refresh_config()
        await self._run_due_scans()

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

    async def _refresh_config(self) -> None:
        try:
            applied = self._state.apply(await self._api.fetch_scan_profiles())
        except Exception:
            # The previous list stays in force. A scanner that forgot its profiles because one
            # request timed out would stop scanning an estate that is still there — the poller's
            # rule, and the reason a profile list is held rather than fetched per scan.
            logger.exception(
                "Scan profile fetch failed; keeping the profiles already held.",
                extra=self._context())
            return

        logger.info(
            "Scan profiles applied.",
            extra=self._context() | {"profile_count": applied.profile_count},
        )

    async def _run_due_scans(self) -> None:
        try:
            due = self._scheduler.due(self._state.profiles.values())
        except Exception:
            logger.exception("Scheduling failed.", extra=self._context())
            return

        for profile in due:
            # Sequentially, one profile at a time. Each scan is already hundreds of probes in
            # flight, and two profiles running together would double that against a network whose
            # capacity this service knows nothing about — the concurrency bound would stop meaning
            # anything.
            try:
                outcome = await self._scanner.scan(profile)
            except Exception:
                logger.exception(
                    "Scan failed.",
                    extra=self._context() | {"profile": profile.name},
                )
                continue

            await self._publish(outcome)

    async def _publish(self, outcome: ScanOutcome) -> None:
        # Logged before publishing and unconditionally, so that a scan that found nothing still
        # says so. "Zero devices out of eight addresses probed" is the difference between a clean
        # sweep of an empty range and a profile whose ranges never expanded, and it is the one line
        # that makes an empty scan verifiable rather than a silence.
        logger.info(
            "Scan complete.",
            extra=self._context() | {
                "profile": outcome.profile_name,
                "scan_id": outcome.scan_id,
                "addresses_probed": outcome.addresses_probed,
                "devices_found": len(outcome.devices),
                "identified": sum(1 for device in outcome.devices if device.identity is not None),
                "neighbours": sum(len(device.neighbours) for device in outcome.devices),
            },
        )

        for problem in outcome.range_errors:
            # A malformed range is a configuration fault somebody has to fix, so it is a warning
            # per range rather than a count — the range text is the only useful part of the
            # message.
            logger.warning(
                "A scan range could not be expanded.",
                extra=self._context() | {"profile": outcome.profile_name, "problem": problem},
            )

        for device in outcome.devices:
            event_id = str(uuid.uuid4())
            envelope = build_envelope(
                build_discovered(device, outcome, self._settings.name, event_id=event_id),
                DISCOVERED_MESSAGE_URN,
                source=self._settings.name,
                message_id=event_id,
            )
            try:
                await self._publisher.publish(envelope)
            except Exception:
                # A discovery that could not be published is lost, and deliberately so: the profile
                # runs again on its own schedule. A scanner that queued discoveries through a
                # broker outage would come back and publish an hour of findings all stamped as if
                # the estate had just changed.
                logger.exception(
                    "Discovery publish failed.",
                    extra=self._context() | {"address": device.address},
                )

    def _context(self) -> dict[str, Any]:
        return {
            "discovery": self._settings.name,
            "discovery_group": self._settings.discovery_group,
            "cycle": self._cycle_number,
        }
