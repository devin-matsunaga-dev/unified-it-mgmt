from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import pytest

from poller.checks import CheckError
from poller.checks.icmp import DEFAULT_COUNT, IcmpCheck


@dataclass(frozen=True)
class FakeHost:
    """Stands in for `icmplib.Host`, which needs a raw socket to produce."""

    is_alive: bool
    avg_rtt: float
    packet_loss: float
    packets_sent: int
    packets_received: int


class RecordingPing:
    def __init__(self, host: FakeHost | Exception) -> None:
        self.host = host
        self.calls: list[dict[str, Any]] = []

    async def __call__(self, address: str, **kwargs: Any) -> FakeHost:
        self.calls.append({"address": address} | kwargs)
        if isinstance(self.host, Exception):
            raise self.host
        return self.host


def metrics_of(outcome: Any) -> dict[str, float | None]:
    return {metric.name: metric.value for metric in outcome.metrics}


async def test_run_a_device_that_answers_reports_round_trip_time_and_no_loss() -> None:
    ping = RecordingPing(FakeHost(True, 12.5, 0.0, 3, 3))

    outcome = await IcmpCheck(ping).run("10.0.0.1", {}, timeout_seconds=6)

    assert outcome.succeeded
    assert outcome.latency_ms == 12.5
    assert metrics_of(outcome) == {
        "icmp.rtt_ms": 12.5,
        "icmp.packet_loss_percent": 0.0,
        "icmp.packets_sent": 3.0,
        "icmp.packets_received": 3.0,
    }


async def test_run_a_device_that_never_answers_fails_but_still_reports_the_loss() -> None:
    ping = RecordingPing(FakeHost(False, 0.0, 1.0, 3, 0))

    outcome = await IcmpCheck(ping).run("10.0.0.9", {}, timeout_seconds=6)

    # The measurement succeeded; the device did not answer it. Both facts have to survive, because
    # 100% loss is what a device-down chart is drawn from.
    assert not outcome.succeeded
    assert "10.0.0.9" in (outcome.error or "")
    assert metrics_of(outcome)["icmp.packet_loss_percent"] == 100.0
    assert "icmp.rtt_ms" not in metrics_of(outcome)


async def test_run_partial_loss_still_succeeds_and_reports_the_percentage() -> None:
    ping = RecordingPing(FakeHost(True, 40.0, 1 / 3, 3, 2))

    outcome = await IcmpCheck(ping).run("10.0.0.1", {}, timeout_seconds=6)

    assert outcome.succeeded
    assert metrics_of(outcome)["icmp.packet_loss_percent"] == pytest.approx(33.33, abs=0.01)


async def test_run_a_socket_failure_raises_a_check_error_naming_the_device() -> None:
    ping = RecordingPing(PermissionError("Root privileges are required"))

    with pytest.raises(CheckError) as raised:
        await IcmpCheck(ping).run("10.0.0.1", {}, timeout_seconds=6)

    assert "10.0.0.1" in str(raised.value)
    assert "Root privileges" in str(raised.value)


async def test_run_divides_the_check_timeout_across_the_packets() -> None:
    ping = RecordingPing(FakeHost(True, 1.0, 0.0, 3, 3))

    await IcmpCheck(ping).run("10.0.0.1", {}, timeout_seconds=6)

    assert ping.calls[0]["count"] == DEFAULT_COUNT
    assert ping.calls[0]["timeout"] == 2.0


async def test_run_a_tiny_timeout_never_drops_a_packet_budget_below_a_second() -> None:
    ping = RecordingPing(FakeHost(True, 1.0, 0.0, 3, 3))

    await IcmpCheck(ping).run("10.0.0.1", {}, timeout_seconds=1)

    # A third of a second is inside the round-trip time of plenty of live WAN links, so dividing
    # blindly would report healthy devices as down.
    assert ping.calls[0]["timeout"] == 1.0


async def test_run_a_count_parameter_is_used() -> None:
    ping = RecordingPing(FakeHost(True, 1.0, 0.0, 5, 5))

    await IcmpCheck(ping).run("10.0.0.1", {"count": "5"}, timeout_seconds=10)

    assert ping.calls[0]["count"] == 5


@pytest.mark.parametrize("count", ["0", "-1", "11", "three", ""])
async def test_run_an_unusable_count_parameter_is_refused_or_defaulted(count: str) -> None:
    ping = RecordingPing(FakeHost(True, 1.0, 0.0, 3, 3))
    check = IcmpCheck(ping)

    if count == "":
        # Blank means "not set", which is the default rather than an error: an operator clearing a
        # parameter field is asking for the default, not for a broken check.
        await check.run("10.0.0.1", {"count": count}, timeout_seconds=6)
        assert ping.calls[0]["count"] == DEFAULT_COUNT
        return

    with pytest.raises(CheckError):
        await check.run("10.0.0.1", {"count": count}, timeout_seconds=6)


async def test_run_the_privileged_flag_is_passed_to_the_socket() -> None:
    ping = RecordingPing(FakeHost(True, 1.0, 0.0, 3, 3))

    await IcmpCheck(ping, privileged=False).run("10.0.0.1", {}, timeout_seconds=6)

    assert ping.calls[0]["privileged"] is False
