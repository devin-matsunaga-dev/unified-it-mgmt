"""Running a cycle's due checks, and deciding what changed about a device because of them."""

from __future__ import annotations

import asyncio
import logging
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from datetime import UTC, datetime

from .checks import CheckError, CheckOutcome, CheckRunner, describe
from .scheduler import DueCheck

logger = logging.getLogger("poller.polling")

ICMP = "icmp"

#: How many checks may be in flight at once. High enough that a cycle over a few hundred devices is
#: bounded by the slowest device rather than by the poller, low enough that a poller does not open a
#: thousand sockets and answer none of them.
DEFAULT_MAX_CONCURRENCY = 50


@dataclass(frozen=True, slots=True)
class CheckResult:
    """One check, run. The unit the telemetry batch is built from."""

    due: DueCheck
    outcome: CheckOutcome
    observed_at: datetime


@dataclass(frozen=True, slots=True)
class ReachabilityChange:
    """A device that started or stopped answering. Only ever produced on the transition."""

    device_id: str
    ci_id: str
    address: str
    is_reachable: bool
    consecutive_failures: int
    error: str | None


@dataclass(frozen=True, slots=True)
class CycleOutcome:
    """Everything one cycle of polling produced."""

    results: tuple[CheckResult, ...] = field(default_factory=tuple)
    changes: tuple[ReachabilityChange, ...] = field(default_factory=tuple)

    @property
    def failed(self) -> int:
        return sum(1 for result in self.results if not result.outcome.succeeded)


class PollingEngine:
    """
    Runs the checks a cycle is due, concurrently and in isolation.

    Isolation is the whole point: every check is awaited inside its own task with its own timeout
    and its own catch, so a device that has gone silent costs exactly its own timeout and nothing
    else in the estate waits for it. This is ARCHITECTURE.md §7.6, and it is the reason nothing here
    raises.
    """

    def __init__(
        self,
        runners: Mapping[str, CheckRunner],
        max_concurrency: int = DEFAULT_MAX_CONCURRENCY,
    ) -> None:
        self._runners = {name.casefold(): runner for name, runner in runners.items()}
        self._limit = asyncio.Semaphore(max_concurrency)
        self._reachable: dict[str, bool] = {}
        self._failures: dict[str, int] = {}

    async def run(self, due: Sequence[DueCheck]) -> CycleOutcome:
        if not due:
            return CycleOutcome()

        results = await asyncio.gather(*[self._run_one(check) for check in due])
        return CycleOutcome(
            results=tuple(results),
            changes=tuple(self._reachability_changes(results)),
        )

    def retain(self, device_ids: set[str]) -> None:
        """
        Forgets the reachability of every device outside `device_ids`.

        Called with the configuration's own devices each cycle. Two reasons: state for a device this
        poller no longer owns is a leak in a process that runs for months, and a device that comes
        back after a spell in another group must be reported as found rather than compared against
        what it was when it left.
        """
        for device_id in self._reachable.keys() - device_ids:
            self._reachable.pop(device_id, None)
            self._failures.pop(device_id, None)

    def forget(self) -> None:
        """Drops every remembered device. Follows a configuration this poller has disowned."""
        self._reachable.clear()
        self._failures.clear()

    async def _run_one(self, due: DueCheck) -> CheckResult:
        observed_at = datetime.now(UTC)
        runner = self._runners.get(due.check_type.casefold())
        if runner is None:
            # A check type this poller does not implement — a TCP or HTTP check (WP-3.8) reaching a
            # poller of this version. Reported rather than dropped, so the gap is visible.
            return CheckResult(
                due,
                CheckOutcome.failure(f"This poller cannot run a '{due.check_type}' check."),
                observed_at,
            )

        async with self._limit:
            try:
                # The runner has its own timeout, and this is the backstop for a runner that does
                # not honour it. Generous by a second so a runner's own timeout wins the race and
                # produces the better message.
                outcome = await asyncio.wait_for(
                    runner.run(due.address, due.parameters, due.timeout_seconds),
                    timeout=due.timeout_seconds + 1,
                )
            except TimeoutError:
                outcome = CheckOutcome.failure(
                    f"'{due.check_name}' against {due.address} did not finish within "
                    f"{due.timeout_seconds:g}s.")
            except CheckError as error:
                outcome = CheckOutcome.failure(str(error))
            except Exception as error:  # one target's failure never ends a cycle
                logger.exception(
                    "Check raised an unexpected error.",
                    extra={"device": due.device_id, "check": due.check_id},
                )
                outcome = CheckOutcome.failure(
                    f"'{due.check_name}' against {due.address} failed: {describe(error)}")

        return CheckResult(due, outcome, observed_at)

    def _reachability_changes(self, results: Sequence[CheckResult]) -> list[ReachabilityChange]:
        changes: list[ReachabilityChange] = []
        for device_id, device_results in _by_device(results).items():
            reachable, error = _reachability_of(device_results)

            if reachable:
                # On a recovery this is the length of the outage that just ended, not zero: the
                # event that says a device came back is the only place a consumer can learn how long
                # it was gone, because the cycles in between publish nothing.
                failures = self._failures.get(device_id, 0)
                self._failures[device_id] = 0
            else:
                failures = self._failures.get(device_id, 0) + 1
                self._failures[device_id] = failures

            previous = self._reachable.get(device_id)
            self._reachable[device_id] = reachable
            if previous == reachable:
                continue

            first = device_results[0].due
            changes.append(ReachabilityChange(
                device_id=device_id,
                ci_id=first.ci_id,
                address=first.address,
                is_reachable=reachable,
                consecutive_failures=failures,
                error=error,
            ))
        return changes


def _by_device(results: Sequence[CheckResult]) -> dict[str, list[CheckResult]]:
    grouped: dict[str, list[CheckResult]] = {}
    for result in results:
        grouped.setdefault(result.due.device_id, []).append(result)
    return grouped


def _reachability_of(results: Sequence[CheckResult]) -> tuple[bool, str | None]:
    """
    Whether a device answered this cycle.

    An ICMP check decides it on its own where there is one: that is what a reachability check is
    for, and an SNMP timeout on a device that still pings is a credential or agent problem rather
    than an outage. Without one, any check that completed proves the device is there.
    """
    pings = [result for result in results if result.due.check_type.casefold() == ICMP]
    deciding = pings if pings else list(results)

    succeeded = [result for result in deciding if result.outcome.succeeded]
    if succeeded:
        return True, None

    errors = [result.outcome.error for result in deciding if result.outcome.error]
    return False, errors[0] if errors else None
