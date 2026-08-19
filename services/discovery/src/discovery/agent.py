"""The cycle: fetch what to scan, scan whatever is due, publish what was found."""

from __future__ import annotations

import asyncio
import logging
import uuid
from typing import Any, Protocol

from .bus import NullPublisher, Publisher, build_envelope
from .config import ConfigState, ScanProfile, parse_profile
from .events import DISCOVERED_MESSAGE_URN, build_discovered
from .scanner import ScanOutcome, ScanProgress
from .scheduler import ScanScheduler
from .settings import Settings

logger = logging.getLogger("discovery.agent")


class ApiClient(Protocol):
    """The API surface the cycle uses, narrowed so tests need no HTTP."""

    async def fetch_scan_profiles(self) -> dict[str, Any]: ...

    async def fetch_scan_runs(self) -> dict[str, Any]: ...

    async def report_scan_run(
        self, scan_run_id: str, result: dict[str, Any]
    ) -> dict[str, Any] | None: ...

    async def report_scan_progress(
        self, scan_run_id: str, progress: dict[str, Any]
    ) -> dict[str, Any] | None: ...


class ScanRunner(Protocol):
    """
    What the cycle asks of a scanner, narrowed so tests need no network.

    A protocol rather than :class:`Scanner` itself, for the reason `ApiClient` is one: the cycle's
    job is scheduling and publishing, and a test of that should not have to construct a sweep.
    """

    async def scan(
        self,
        profile: ScanProfile,
        on_progress: ScanProgress | None = None,
    ) -> ScanOutcome: ...


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
        self._progress_tasks: set[asyncio.Task[None]] = set()

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
        # After the scheduled work rather than before it, so a queue of requests cannot starve the
        # estate's own sweeps. A requested run waits at most one more cycle for that.
        await self._run_requested_scans()

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
            extra=self._context() | {
                "profile_count": applied.profile_count,
                # Logged every time rather than only when it changes: "why is nothing being
                # scanned" is the question this line exists to answer, and an operator reading the
                # log an hour later should not have to find the cycle where it flipped.
                "scheduled_scanning_enabled": applied.scheduled_scanning_enabled,
            },
        )

    async def _run_due_scans(self) -> None:
        try:
            # `scheduled()` is what the switches mean in practice: the estate-wide one empties the
            # list, and a profile with its own schedule off is left out of it. Both are applied
            # before the scheduler sees anything, so a profile that is not scheduled never takes a
            # due time and cannot come back overdue the moment somebody switches it on.
            due = self._scheduler.due(self._state.scheduled())
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

    async def _run_requested_scans(self) -> None:
        """
        Runs whatever a person has asked for, and tells the platform how it went.

        Nothing here is retried. A run this cycle could not report on is one the platform's own
        sweeper will time out, and sweeping the same range again to produce a second result for a
        row that already has one would make the honest answer harder to find.
        """
        try:
            payload = await self._api.fetch_scan_runs()
        except Exception:
            # Exactly like the config fetch above: a scanner that stopped because the API blinked
            # is one somebody has to go and start again.
            logger.exception("Requested scan fetch failed.", extra=self._context())
            return

        raw = payload.get("runs") or []
        if not raw:
            return

        logger.info(
            "Requested scans claimed.",
            extra=self._context() | {"requested_scans": len(raw)},
        )

        for entry in raw:
            try:
                run_id = str(entry["scanRunId"])
                profile = parse_profile(entry["profile"])
            except Exception:
                # A claimed run that cannot be read is left alone rather than reported failed: the
                # platform times it out, which is the truthful record of a scanner that could not
                # understand what it was handed.
                logger.exception(
                    "A claimed scan run could not be read; it was left alone.",
                    extra=self._context())
                continue

            if profile is None:
                await self._report(run_id, {
                    "outcome": "Failed",
                    "error": "The profile carried no id or no scannable range.",
                })
                continue

            try:
                outcome = await self._scanner.scan(
                    profile, on_progress=self._progress_reporter(run_id))
            except Exception:
                logger.exception(
                    "Requested scan failed.",
                    extra=self._context() | {"profile": profile.name, "scan_run": run_id},
                )
                await self._report(run_id, {
                    "outcome": "Failed",
                    "error": "The scan raised. See the scanner's log for the cycle that ran it.",
                })
                continue

            # Let the progress posts still in flight land before the result does. They would be
            # refused anyway once the run leaves Running — progress cannot move a finished row — but
            # draining keeps the ordering honest rather than relying on that refusal.
            await self._drain_progress()

            await self._publish(outcome)
            await self._report(run_id, {
                "outcome": "Succeeded",
                "addressesProbed": outcome.addresses_probed,
                "devicesFound": len(outcome.devices),
                # A range that would not expand is a configuration fault somebody has to see, and
                # the run they are watching is the place they will look for it.
                "error": "; ".join(outcome.range_errors) or None,
            })

    def _progress_reporter(self, run_id: str) -> ScanProgress:
        """
        Turns the scanner's synchronous progress callback into a fire-and-forget POST.

        A task rather than an await, because the callback runs inside the sweep's own concurrency
        and blocking it on an HTTP round trip would slow the very thing it measures. Nothing waits
        on the task and nothing retries it: a progress post is disposable by definition — the next
        one supersedes it, and the result at the end is what the run is judged on.
        """
        def report(probed: int, total: int, last_address: str | None) -> None:
            payload: dict[str, Any] = {
                "addressesProbed": probed,
                "addressesTotal": total,
                "lastRespondingAddress": last_address,
            }
            task = asyncio.create_task(self._post_progress(run_id, payload))
            # Held so the loop cannot garbage-collect a task nobody awaits, and discarded when it
            # finishes. Without this, progress posts vanish mid-flight under load.
            self._progress_tasks.add(task)
            task.add_done_callback(self._progress_tasks.discard)

        return report

    async def _drain_progress(self) -> None:
        """Waits for outstanding progress posts. Never raises: each one already swallows its own."""
        if not self._progress_tasks:
            return
        await asyncio.gather(*tuple(self._progress_tasks), return_exceptions=True)

    async def _post_progress(self, run_id: str, payload: dict[str, Any]) -> None:
        try:
            await self._api.report_scan_progress(run_id, payload)
        except Exception:
            # Debug, not exception: progress is disposable, and a scan of a /24 that lost the broker
            # would otherwise write a stack trace a second into the log somebody needs to read.
            logger.debug(
                "A progress report could not be sent.",
                extra=self._context() | {"scan_run": run_id},
            )

    async def _report(self, run_id: str, result: dict[str, Any]) -> None:
        try:
            await self._api.report_scan_run(run_id, result)
        except Exception:
            logger.exception(
                "A scan result could not be reported; the platform will time it out.",
                extra=self._context() | {"scan_run": run_id},
            )

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
