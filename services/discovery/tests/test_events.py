from __future__ import annotations

from datetime import UTC, datetime

from discovery.events import build_discovered
from discovery.identify import CDP, LLDP, Neighbour, SnmpIdentity
from discovery.scanner import DiscoveredDevice, ScanOutcome

#: Every property of `Contracts.Events.DeviceDiscovered`, in the spelling the consumer's serializer
#: reads. A property the publisher omits arrives as a default — false on a boolean, empty on a list
#: — which is a silent wrong answer rather than an error.
CONTRACT_FIELDS = {
    "eventId",
    "occurredAt",
    "discoveryName",
    "scanProfileId",
    "scanProfileName",
    "scanId",
    "address",
    "hostname",
    "hostnameSource",
    "respondedToPing",
    "openPorts",
    "snmp",
    "neighbours",
}

IDENTITY_FIELDS = {
    "sysName",
    "sysDescription",
    "sysObjectId",
    "sysLocation",
    "sysContact",
    "uptimeSeconds",
}

NEIGHBOUR_FIELDS = {"protocol", "localPort", "remoteSystemName", "remotePort", "remoteAddress"}


def outcome() -> ScanOutcome:
    return ScanOutcome(
        profile_id="0199c0de-4100-7000-8000-000000000001",
        profile_name="Local subnet sweep",
        scan_id="7f5a4b3c-0000-4000-8000-00000000000a",
        addresses_probed=6,
    )


def device(**overrides: object) -> DiscoveredDevice:
    defaults: dict[str, object] = {
        "address": "10.0.0.2",
        "responded_to_ping": True,
        "open_ports": (22, 443),
        "hostname": "sim-switch.example.test",
        "identity": SnmpIdentity(
            # A value that appears nowhere else in the fixture, so the assertion below is a real
            # search. WP-3.11's checklist made the opposite mistake and grepped for `healthy`,
            # which the seeded credentials are *named* after — the grep could never have failed.
            community="tr0ub4dor-community",
            sys_name="sim-switch-healthy",
            sys_description="IT Platform simulated switch, healthy profile",
            sys_object_id="1.3.6.1.4.1.8072.3.2.10",
            sys_location="Primary Data Centre",
            sys_contact="itops@example.com",
            uptime_seconds=5_184_000.0,
        ),
        "neighbours": (
            Neighbour(
                protocol=LLDP,
                local_port="GigabitEthernet0/1",
                remote_system_name="dc1-core-rtr-01",
                remote_port="GigabitEthernet0/24",
            ),
            Neighbour(
                protocol=CDP,
                local_port="GigabitEthernet0/3",
                remote_system_name="dc1-core-sw-02",
                remote_port="GigabitEthernet0/23",
                remote_address="10.0.0.1",
            ),
        ),
    }
    return DiscoveredDevice(**(defaults | overrides))  # type: ignore[arg-type]


def test_build_discovered_carries_every_property_of_the_contract() -> None:
    payload = build_discovered(device(), outcome(), "discovery-1")

    assert set(payload) == CONTRACT_FIELDS
    assert set(payload["snmp"]) == IDENTITY_FIELDS
    assert set(payload["neighbours"][0]) == NEIGHBOUR_FIELDS


def test_build_discovered_never_carries_the_community_that_answered() -> None:
    payload = build_discovered(device(), outcome(), "discovery-1")

    # The one thing a scan learns that is a secret in a real estate. This event travels the bus and
    # lands in a review queue somebody reads; the vault exists so that secrets are not fields of
    # events. The scanner logs the community's position in its own list instead.
    assert "community" not in payload["snmp"]
    assert "tr0ub4dor-community" not in str(payload)


def test_build_discovered_reads_the_scan_it_came_from() -> None:
    payload = build_discovered(device(), outcome(), "discovery-1")

    assert payload["discoveryName"] == "discovery-1"
    assert payload["scanProfileId"] == "0199c0de-4100-7000-8000-000000000001"
    assert payload["scanProfileName"] == "Local subnet sweep"
    assert payload["scanId"] == "7f5a4b3c-0000-4000-8000-00000000000a"


def test_build_discovered_for_a_device_that_answered_no_community_carries_a_null_identity() -> None:
    payload = build_discovered(
        device(identity=None, neighbours=()), outcome(), "discovery-1")

    # Null rather than an empty object: an identity present but blank would look downstream like a
    # device that identified itself as nothing.
    assert payload["snmp"] is None
    assert payload["neighbours"] == []


def test_build_discovered_for_a_port_only_host_says_it_did_not_answer_a_ping() -> None:
    payload = build_discovered(
        device(responded_to_ping=False, identity=None, neighbours=()), outcome(), "discovery-1")

    assert payload["respondedToPing"] is False
    assert payload["openPorts"] == [22, 443]


def test_build_discovered_stamps_the_ids_and_time_it_is_given() -> None:
    moment = datetime(2026, 8, 13, 4, 5, 6, tzinfo=UTC)

    payload = build_discovered(
        device(), outcome(), "discovery-1", event_id="abc", occurred_at=moment)

    assert payload["eventId"] == "abc"
    assert payload["occurredAt"] == "2026-08-13T04:05:06+00:00"
