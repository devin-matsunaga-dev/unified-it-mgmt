"""When each scan profile is next due, and which of them a given cycle should run."""

from __future__ import annotations

import time
from collections.abc import Callable, Iterable
from dataclasses import dataclass, field

from .config import ScanProfile


@dataclass(slots=True)
class ScanScheduler:
    """
    Per-profile due times over a cycle that ticks at the scanner's own interval.

    The same shape as the poller's `CheckScheduler`, and for the same reasons. A profile runs when
    its own interval says so rather than when the cycle comes round, so an hourly sweep on a
    thirty-second cycle costs a hundred and nineteen cycles of nothing. A profile seen for the
    first time is due immediately, so one added to the configuration is scanned by the cycle that
    learns about it.

    Due times are held against a monotonic clock: the wall clock moving — an NTP correction, a
    container resuming — must not make every profile in the estate either overdue or an hour early.
    """

    clock: Callable[[], float] = time.monotonic
    _due_at: dict[str, float] = field(default_factory=dict)

    def due(self, profiles: Iterable[ScanProfile]) -> list[ScanProfile]:
        """
        Everything due now, and it marks them run.

        Marked here rather than when the scan finishes, exactly as the poller marks a check: a
        sweep that runs long must not be started again by the next cycle, and a profile's interval
        is a period rather than a gap between finishing and starting.
        """
        now = self.clock()
        due: list[ScanProfile] = []
        live: set[str] = set()

        for profile in profiles:
            live.add(profile.profile_id)
            if now < self._due_at.get(profile.profile_id, 0.0):
                continue
            self._due_at[profile.profile_id] = now + profile.interval_seconds
            due.append(profile)

        # A profile that has been deleted, disabled or moved to another group would otherwise keep
        # a due time forever, in a process that runs for months.
        for forgotten in self._due_at.keys() - live:
            del self._due_at[forgotten]

        return due

    def forget(self) -> None:
        """Drops every due time, so the next cycle runs everything."""
        self._due_at.clear()
