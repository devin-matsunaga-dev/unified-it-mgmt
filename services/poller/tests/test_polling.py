from __future__ import annotations

import asyncio
from collections.abc import Mapping

from poller.checks import CheckError, CheckOutcome, Metric
from poller.polling import PollingEngine
from poller.scheduler import DueCheck


def due(
    check_id: str,
    device_id: str = "d1",
    check_type: str = "Icmp",
    timeout: float = 5,
) -> DueCheck:
    return DueCheck(
        device_id=device_id,
        ci_id=f"ci-{device_id}",
        address=f"10.0.0.{device_id[-1]}",
        check_id=check_id,
        check_type=check_type,
        check_name=f"{check_type} check",
        interval_seconds=60,
        timeout_seconds=timeout,
        parameters={},
    )


class StubCheck:
    """Answers with whatever the test decided, and records that it was asked."""

    def __init__(self, outcome: CheckOutcome | Exception, delay: float = 0.0) -> None:
        # Mutable, so a test can make a device stop answering between cycles the way one does.
        self.outcome = outcome
        self.delay = delay
        self.calls = 0

    async def run(
        self, address: str, parameters: Mapping[str, str], timeout_seconds: float,
    ) -> CheckOutcome:
        self.calls += 1
        if self.delay:
            await asyncio.sleep(self.delay)
        if isinstance(self.outcome, Exception):
            raise self.outcome
        return self.outcome


UP = CheckOutcome(succeeded=True, latency_ms=5.0, metrics=(Metric("icmp.rtt_ms", value=5.0),))
DOWN = CheckOutcome.failure("no reply")


async def test_run_returns_one_result_per_due_check() -> None:
    engine = PollingEngine({"Icmp": StubCheck(UP)})

    outcome = await engine.run([due("c1"), due("c2", device_id="d2")])

    assert len(outcome.results) == 2
    assert all(result.outcome.succeeded for result in outcome.results)


async def test_run_with_nothing_due_does_no_work_and_reports_nothing() -> None:
    check = StubCheck(UP)

    outcome = await PollingEngine({"Icmp": check}).run([])

    assert outcome.results == ()
    assert check.calls == 0


async def test_run_a_check_that_raises_fails_only_itself() -> None:
    engine = PollingEngine({
        "Icmp": StubCheck(UP),
        "Snmp": StubCheck(RuntimeError("the library exploded")),
    })

    outcome = await engine.run([due("c1"), due("c2", device_id="d2", check_type="Snmp")])

    # ARCHITECTURE §7.6: one dead device never blocks a cycle. An unexpected exception from a
    # library is the case that rule exists for, so it is caught in the same place as a timeout.
    succeeded = {result.due.check_id: result.outcome.succeeded for result in outcome.results}
    assert succeeded == {"c1": True, "c2": False}


async def test_run_a_check_that_hangs_is_cut_off_and_the_rest_still_report() -> None:
    engine = PollingEngine({
        "Icmp": StubCheck(UP),
        "Snmp": StubCheck(UP, delay=5),
    })

    outcome = await asyncio.wait_for(
        engine.run([due("c1"), due("c2", device_id="d2", check_type="Snmp", timeout=0.05)]),
        timeout=3,
    )

    hung = next(result for result in outcome.results if result.due.check_id == "c2")
    assert not hung.outcome.succeeded
    assert "did not finish" in (hung.outcome.error or "")


async def test_run_a_check_error_becomes_its_own_message_rather_than_a_stack_trace() -> None:
    engine = PollingEngine({"Snmp": StubCheck(CheckError("SNMP v3 needs a 'securityName'."))})

    outcome = await engine.run([due("c1", check_type="Snmp")])

    assert outcome.results[0].outcome.error == "SNMP v3 needs a 'securityName'."


async def test_run_a_check_type_this_poller_cannot_run_is_reported_not_dropped() -> None:
    engine = PollingEngine({"Icmp": StubCheck(UP)})

    outcome = await engine.run([due("c1", check_type="Http")])

    # WP-3.8's checks reaching a poller of this version: visible as a gap rather than as silence.
    assert not outcome.results[0].outcome.succeeded
    assert "cannot run a 'Http' check" in (outcome.results[0].outcome.error or "")


# --- reachability --------------------------------------------------------------------------------

async def test_run_the_first_observation_of_a_device_is_reported_as_a_change() -> None:
    engine = PollingEngine({"Icmp": StubCheck(UP)})

    outcome = await engine.run([due("c1")])

    # The platform has no state for a device it has never polled, so the poller states what it
    # found. The cost is one event per device on a restart.
    change = outcome.changes[0]
    assert (change.device_id, change.is_reachable) == ("d1", True)


async def test_run_a_device_that_stays_up_reports_no_further_changes() -> None:
    engine = PollingEngine({"Icmp": StubCheck(UP)})

    await engine.run([due("c1")])
    second = await engine.run([due("c1")])

    assert second.changes == ()


async def test_run_a_device_going_down_and_coming_back_reports_exactly_two_changes() -> None:
    check = StubCheck(UP)
    engine = PollingEngine({"Icmp": check})

    await engine.run([due("c1")])
    check.outcome = DOWN
    went_down = await engine.run([due("c1")])
    still_down = await engine.run([due("c1")])
    check.outcome = UP
    came_back = await engine.run([due("c1")])

    # A device that is down for an hour says so once. The alert engine (WP-3.5) is the thing that
    # decides how long an outage has to last to matter, and it cannot do that from a repeated fact.
    assert [change.is_reachable for change in went_down.changes] == [False]
    assert still_down.changes == ()
    assert [change.is_reachable for change in came_back.changes] == [True]


async def test_run_the_recovery_event_reports_how_long_the_outage_was() -> None:
    check = StubCheck(DOWN)
    engine = PollingEngine({"Icmp": check})

    went_down = await engine.run([due("c1")])
    for _ in range(3):
        await engine.run([due("c1")])
    check.outcome = UP
    came_back = await engine.run([due("c1")])

    assert went_down.changes[0].consecutive_failures == 1
    # The cycles in between publish nothing, so this event is the only place the length of the
    # outage can be read.
    assert came_back.changes[0].consecutive_failures == 4


async def test_run_an_icmp_check_decides_reachability_over_a_failing_snmp_check() -> None:
    engine = PollingEngine({"Icmp": StubCheck(UP), "Snmp": StubCheck(DOWN)})

    outcome = await engine.run([due("ping"), due("cpu", check_type="Snmp")])

    # A device that pings but will not answer SNMP has a credential or agent problem, not an outage.
    assert outcome.changes[0].is_reachable is True


async def test_run_without_an_icmp_check_any_answer_proves_the_device_is_there() -> None:
    engine = PollingEngine({"Snmp": StubCheck(UP)})

    outcome = await engine.run([due("cpu", check_type="Snmp")])

    assert outcome.changes[0].is_reachable is True


async def test_run_a_device_whose_every_check_fails_is_unreachable_with_a_reason() -> None:
    engine = PollingEngine({"Snmp": StubCheck(DOWN)})

    outcome = await engine.run([due("cpu", check_type="Snmp")])

    assert outcome.changes[0].is_reachable is False
    assert outcome.changes[0].error == "no reply"


async def test_retain_forgets_a_device_that_left_the_configuration() -> None:
    engine = PollingEngine({"Icmp": StubCheck(UP)})

    await engine.run([due("c1")])
    engine.retain(set())
    returned = await engine.run([due("c1")])

    # A device that comes back after a spell in another group is reported as found, rather than
    # compared against what it was when it left.
    assert [change.is_reachable for change in returned.changes] == [True]


async def test_retain_keeps_a_device_that_is_still_configured() -> None:
    engine = PollingEngine({"Icmp": StubCheck(UP)})

    await engine.run([due("c1")])
    engine.retain({"d1"})

    assert (await engine.run([due("c1")])).changes == ()


async def test_run_honours_the_concurrency_limit() -> None:
    in_flight = 0
    peak = 0

    class Counting:
        async def run(
            self, address: str, parameters: Mapping[str, str], timeout_seconds: float,
        ) -> CheckOutcome:
            nonlocal in_flight, peak
            in_flight += 1
            peak = max(peak, in_flight)
            await asyncio.sleep(0.01)
            in_flight -= 1
            return UP

    engine = PollingEngine({"Icmp": Counting()}, max_concurrency=2)

    await engine.run([due(f"c{index}", device_id=f"d{index}") for index in range(6)])

    # A poller that opens a thousand sockets at once answers none of them.
    assert peak <= 2
