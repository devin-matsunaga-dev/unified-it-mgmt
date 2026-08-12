"""When each check is next due, and which of them a given cycle should run."""

from __future__ import annotations

import time
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass, field
from typing import Any

#: Used when a check arrives without one. The server validates intervals (WP-3.1's `CheckRules`), so
#: this only covers a check that reached the poller through some other route.
DEFAULT_INTERVAL_SECONDS = 60

#: Used when a check carries no timeout. Deliberately below the default interval: a check that can
#: run longer than its own period would queue behind itself forever.
DEFAULT_TIMEOUT_SECONDS = 10


@dataclass(frozen=True, slots=True)
class DueCheck:
    """One check to run against one device, with everything the runner and the telemetry need."""

    device_id: str
    ci_id: str
    address: str
    check_id: str
    check_type: str
    check_name: str
    interval_seconds: int
    timeout_seconds: float
    parameters: Mapping[str, str]
    #: The vault credential this check authenticates with, or "" for one that authenticates to
    #: nothing. An id only — the material is fetched separately and merged in by `CredentialStore`
    #: just before the check runs, so it never lives in the configuration this poller caches.
    credential_id: str = ""


@dataclass(slots=True)
class CheckScheduler:
    """
    Per-check due times over a cycle that ticks at the poller's own interval.

    A check runs when its own interval says so, not when the cycle happens to come round: a
    five-minute CPU check on a fifteen-second cycle should cost twenty cycles of nothing. A check
    seen for the first time is due immediately, so a device added to the configuration is polled in
    the cycle that learns about it rather than one interval later.

    Due times are held against a monotonic clock, because the wall clock moving — an NTP correction,
    a container resuming — must not make every check in the estate either overdue or an hour early.
    """

    clock: Callable[[], float] = time.monotonic
    _due_at: dict[str, float] = field(default_factory=dict)

    def due(self, devices: Iterable[Mapping[str, Any]]) -> list[DueCheck]:
        """
        Everything due now, and it marks them run.

        Marking here rather than after the check completes is deliberate: a check that hangs until
        its timeout must not be started again by the next cycle, and the interval is a period rather
        than a gap between finishing and starting.
        """
        now = self.clock()
        due: list[DueCheck] = []
        live: set[str] = set()

        for device in devices:
            device_id = str(device.get("deviceId") or "")
            address = str(device.get("address") or "")
            if not device_id or not address:
                # A device with no address is one nothing can be polled against. The API requires
                # one, so this is a malformed payload rather than a state to report on.
                continue

            for check in device.get("checks") or []:
                check_id = str(check.get("checkId") or "")
                if not check_id:
                    continue
                live.add(check_id)
                interval = _positive(check.get("intervalSeconds"), DEFAULT_INTERVAL_SECONDS)
                if now < self._due_at.get(check_id, 0.0):
                    continue
                self._due_at[check_id] = now + interval
                due.append(DueCheck(
                    device_id=device_id,
                    ci_id=str(device.get("ciId") or ""),
                    address=address,
                    check_id=check_id,
                    check_type=str(check.get("type") or ""),
                    check_name=str(check.get("name") or ""),
                    interval_seconds=interval,
                    timeout_seconds=float(
                        _positive(check.get("timeoutSeconds"), DEFAULT_TIMEOUT_SECONDS)),
                    parameters=dict(check.get("parameters") or {}),
                    credential_id=str(check.get("credentialId") or ""),
                ))

        # A check that has been deleted, disabled or moved to another poller keeps a due time
        # forever otherwise — one entry per check the estate has ever had, in a process that runs
        # for months.
        for forgotten in self._due_at.keys() - live:
            del self._due_at[forgotten]

        return due

    def forget(self) -> None:
        """Drops every due time, so the next cycle runs everything. Follows a config reset."""
        self._due_at.clear()


def _positive(raw: Any, default: int) -> int:
    try:
        value = int(raw)
    except (TypeError, ValueError):
        return default
    return value if value > 0 else default
