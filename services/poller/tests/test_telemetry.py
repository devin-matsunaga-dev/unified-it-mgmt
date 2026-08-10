from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from poller.bus import build_envelope
from poller.checks import CheckOutcome, Metric
from poller.polling import CheckResult, ReachabilityChange
from poller.scheduler import DueCheck
from poller.telemetry import (
    REACHABILITY_MESSAGE_URN,
    TELEMETRY_MESSAGE_URN,
    build_reachability,
    build_telemetry,
)

FIXTURES = Path(__file__).parent / "fixtures"
POLLER = "POLLER_NAME_PLACEHOLDER"
MOMENT = datetime(2026, 8, 10, 12, 0, 0, tzinfo=UTC)

DEVICE_ID = "0199c0de-0000-7000-8000-00000000d001"
CI_ID = "0199c0de-0000-7000-8000-00000000c001"
PING_CHECK_ID = "0199c0de-0000-7000-8000-0000000c4ec1"
CPU_CHECK_ID = "0199c0de-0000-7000-8000-0000000c4ec2"


def _due(check_id: str, check_type: str, name: str) -> DueCheck:
    return DueCheck(
        device_id=DEVICE_ID,
        ci_id=CI_ID,
        address="10.10.20.31",
        check_id=check_id,
        check_type=check_type,
        check_name=name,
        interval_seconds=60,
        timeout_seconds=5,
        parameters={},
    )


def results() -> list[CheckResult]:
    """One check that measured something and one that failed — both have to survive the wire."""
    return [
        CheckResult(
            _due(PING_CHECK_ID, "Icmp", "Reachability"),
            CheckOutcome(
                succeeded=True,
                latency_ms=1.42,
                metrics=(
                    Metric("icmp.rtt_ms", value=1.42, unit="ms"),
                    Metric("icmp.packet_loss_percent", value=0.0, unit="%"),
                ),
            ),
            observed_at=MOMENT,
        ),
        CheckResult(
            _due(CPU_CHECK_ID, "Snmp", "SNMP: CPU"),
            CheckOutcome.failure("The agent at 10.10.20.31 did not answer within 5s."),
            observed_at=MOMENT,
        ),
    ]


def telemetry_envelope() -> dict[str, Any]:
    return build_envelope(
        build_telemetry(
            results(),
            poller_name=POLLER,
            poller_group="default",
            cycle_number=7,
            event_id="0199c0de-0000-7000-8000-00000000fee1",
            occurred_at=MOMENT,
        ),
        TELEMETRY_MESSAGE_URN,
        source=POLLER,
        message_id="0199c0de-0000-7000-8000-00000000fee1",
        sent_time=MOMENT,
        conversation_id="0199c0de-0000-7000-8000-0000000c0ffe",
    )


def reachability_envelope() -> dict[str, Any]:
    return build_envelope(
        build_reachability(
            ReachabilityChange(
                device_id=DEVICE_ID,
                ci_id=CI_ID,
                address="10.10.20.31",
                is_reachable=False,
                consecutive_failures=2,
                error="No reply from 10.10.20.31 after 3 packets.",
            ),
            poller_name=POLLER,
            poller_group="default",
            event_id="0199c0de-0000-7000-8000-00000000dead",
            occurred_at=MOMENT,
        ),
        REACHABILITY_MESSAGE_URN,
        source=POLLER,
        message_id="0199c0de-0000-7000-8000-00000000dead",
        sent_time=MOMENT,
        conversation_id="0199c0de-0000-7000-8000-0000000c0ffe",
    )


# --- the cross-language contract -----------------------------------------------------------------

def test_telemetry_envelope_matches_the_fixture_the_dotnet_consumer_is_tested_against() -> None:
    # Same rule as the heartbeat: each side testing its own idea of the envelope is exactly how a
    # dead-lettering bug passed a green suite. If this fails, regenerate the fixture and run both.
    assert telemetry_envelope() == json.loads((FIXTURES / "telemetry-envelope.json").read_text())


def test_reachability_envelope_matches_the_fixture_the_dotnet_consumer_is_tested_against() -> None:
    assert reachability_envelope() == json.loads(
        (FIXTURES / "reachability-envelope.json").read_text())


def test_envelopes_carry_no_address_fields() -> None:
    for envelope in (telemetry_envelope(), reachability_envelope()):
        # MassTransit parses these as absolute URIs before any consumer runs, and every exchange
        # name here contains a colon.
        assert "sourceAddress" not in envelope
        assert "destinationAddress" not in envelope


def test_envelope_message_types_are_the_urns_masstransit_routes_on() -> None:
    assert telemetry_envelope()["messageType"] == [
        "urn:message:Contracts.Events:DeviceTelemetryReported"]
    assert reachability_envelope()["messageType"] == [
        "urn:message:Contracts.Events:DeviceReachabilityChanged"]


# --- payload shape -------------------------------------------------------------------------------

def test_build_telemetry_batches_a_whole_cycle_into_one_message() -> None:
    payload = build_telemetry(results(), poller_name=POLLER, poller_group="default", cycle_number=7)

    # Two hundred devices at four checks each would otherwise be eight hundred messages a cycle.
    assert len(payload["results"]) == 2
    assert payload["cycleNumber"] == 7


def test_build_telemetry_keeps_a_failed_check_and_its_reason() -> None:
    payload = build_telemetry(results(), poller_name=POLLER, poller_group="default", cycle_number=7)

    failed = payload["results"][1]
    # A timeout is a fact about the device. Dropping it would make an unreachable device look like
    # one nobody asked about.
    assert failed["succeeded"] is False
    assert "did not answer" in failed["error"]
    assert failed["metrics"] == []


def test_build_telemetry_carries_a_metrics_value_and_unit_separately_from_text() -> None:
    payload = build_telemetry(results(), poller_name=POLLER, poller_group="default", cycle_number=7)

    metric = payload["results"][0]["metrics"][0]
    assert (metric["name"], metric["value"], metric["unit"]) == ("icmp.rtt_ms", 1.42, "ms")
    # A hypertable stores numbers and a device record stores names; WP-3.4 tells them apart here.
    assert metric["text"] is None


def test_build_telemetry_of_an_empty_cycle_is_still_a_well_formed_message() -> None:
    payload = build_telemetry([], poller_name=POLLER, poller_group="default", cycle_number=1)

    assert payload["results"] == []
    assert payload["pollerName"] == POLLER


def test_build_reachability_states_the_device_the_address_and_why() -> None:
    payload = json.loads(json.dumps(reachability_envelope()["message"]))

    assert payload["deviceId"] == DEVICE_ID
    assert payload["ciId"] == CI_ID
    assert payload["isReachable"] is False
    assert payload["consecutiveFailures"] == 2
    assert "No reply" in payload["error"]


def test_build_reachability_of_a_recovery_carries_no_error() -> None:
    payload = build_reachability(
        ReachabilityChange(DEVICE_ID, CI_ID, "10.10.20.31", True, 0, None),
        poller_name=POLLER,
        poller_group="default",
    )

    assert payload["isReachable"] is True
    assert payload["error"] is None
