from __future__ import annotations

from typing import Any

from poller.scheduler import DEFAULT_INTERVAL_SECONDS, CheckScheduler


class FakeClock:
    """A monotonic clock the test moves by hand."""

    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += seconds


def device(device_id: str, *checks: dict[str, Any], address: str = "10.0.0.1") -> dict[str, Any]:
    return {
        "deviceId": device_id,
        "ciId": f"ci-{device_id}",
        "address": address,
        "checks": list(checks),
    }


def check(check_id: str, interval: int = 60, check_type: str = "Icmp") -> dict[str, Any]:
    return {
        "checkId": check_id,
        "type": check_type,
        "name": f"{check_type} {check_id}",
        "intervalSeconds": interval,
        "timeoutSeconds": 5,
        "parameters": {},
    }


def test_due_a_check_seen_for_the_first_time_runs_immediately() -> None:
    scheduler = CheckScheduler(clock=FakeClock())

    due = scheduler.due([device("d1", check("c1", interval=300))])

    # A device added to the configuration is polled by the cycle that learns about it, not one
    # five-minute interval later.
    assert [item.check_id for item in due] == ["c1"]


def test_due_a_check_is_not_run_again_until_its_own_interval_has_passed() -> None:
    clock = FakeClock()
    scheduler = CheckScheduler(clock=clock)
    devices = [device("d1", check("c1", interval=60))]

    scheduler.due(devices)
    clock.advance(15)
    fifteen_seconds_later = scheduler.due(devices)
    clock.advance(45)
    a_minute_later = scheduler.due(devices)

    # This is the whole point of the scheduler: a 60s check on a 15s cycle costs three cycles of
    # nothing.
    assert fifteen_seconds_later == []
    assert [item.check_id for item in a_minute_later] == ["c1"]


def test_due_checks_on_one_device_keep_their_own_intervals() -> None:
    clock = FakeClock()
    scheduler = CheckScheduler(clock=clock)
    devices = [device(
        "d1", check("ping", interval=30), check("cpu", interval=300, check_type="Snmp"))]

    scheduler.due(devices)
    clock.advance(30)
    due = scheduler.due(devices)

    assert [item.check_id for item in due] == ["ping"]


def test_due_carries_everything_the_runner_and_the_telemetry_need() -> None:
    scheduler = CheckScheduler(clock=FakeClock())
    devices = [device("d1", {
        "checkId": "c1",
        "type": "Snmp",
        "name": "SNMP: CPU",
        "intervalSeconds": 60,
        "timeoutSeconds": 4,
        "parameters": {"metric": "cpu", "community": "public"},
    }, address="10.0.0.7")]

    item = scheduler.due(devices)[0]

    assert (item.device_id, item.ci_id, item.address) == ("d1", "ci-d1", "10.0.0.7")
    assert (item.check_type, item.check_name, item.timeout_seconds) == ("Snmp", "SNMP: CPU", 4.0)
    assert item.parameters == {"metric": "cpu", "community": "public"}


def test_due_marks_a_check_run_when_it_is_handed_out_not_when_it_finishes() -> None:
    clock = FakeClock()
    scheduler = CheckScheduler(clock=clock)
    devices = [device("d1", check("c1", interval=60))]

    scheduler.due(devices)
    clock.advance(59)

    # A check that hangs until its timeout must not be started again by the next cycle: the interval
    # is a period, not a gap between finishing and starting.
    assert scheduler.due(devices) == []


def test_due_a_device_with_no_address_is_skipped_rather_than_polled() -> None:
    scheduler = CheckScheduler(clock=FakeClock())

    due = scheduler.due([{"deviceId": "d1", "ciId": "ci", "address": "", "checks": [check("c1")]}])

    assert due == []


def test_due_a_check_with_no_interval_falls_back_to_a_default() -> None:
    clock = FakeClock()
    scheduler = CheckScheduler(clock=clock)
    devices = [device("d1", {"checkId": "c1", "type": "Icmp", "name": "ping"})]

    scheduler.due(devices)
    clock.advance(DEFAULT_INTERVAL_SECONDS - 1)
    before = scheduler.due(devices)
    clock.advance(1)
    after = scheduler.due(devices)

    assert before == []
    assert len(after) == 1


def test_due_a_check_that_leaves_the_configuration_is_forgotten() -> None:
    clock = FakeClock()
    scheduler = CheckScheduler(clock=clock)

    scheduler.due([device("d1", check("c1", interval=3600))])
    scheduler.due([device("d1")])
    returned = scheduler.due([device("d1", check("c1", interval=3600))])

    # Otherwise a poller running for months holds one due time per check the estate has ever had —
    # and a check that comes back waits out an hour it spent deleted.
    assert [item.check_id for item in returned] == ["c1"]


def test_forget_makes_every_check_due_again() -> None:
    clock = FakeClock()
    scheduler = CheckScheduler(clock=clock)
    devices = [device("d1", check("c1", interval=3600))]

    scheduler.due(devices)
    scheduler.forget()

    assert [item.check_id for item in scheduler.due(devices)] == ["c1"]
