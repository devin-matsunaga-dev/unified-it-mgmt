"""Building the `DeviceDiscovered` payload the .NET consumer reads."""

from __future__ import annotations

import uuid
from datetime import UTC, datetime
from typing import Any

from .identify import Neighbour, SnmpIdentity
from .scanner import DiscoveredDevice, ScanOutcome

#: Kept beside the builder rather than imported from `bus`, so the payload's shape and the URN it
#: is published under are read in one place.
DISCOVERED_MESSAGE_URN = "urn:message:Contracts.Events:DeviceDiscovered"


def build_discovered(
    device: DiscoveredDevice,
    outcome: ScanOutcome,
    discovery_name: str,
    event_id: str | None = None,
    occurred_at: datetime | None = None,
) -> dict[str, Any]:
    """
    One device, as `Contracts.Events.DeviceDiscovered` expects it.

    Every property of the contract is present in every message, including the ones that are null: a
    field the publisher omits arrives at the consumer as a default, which on a boolean reads as
    false and on a list as empty — a silent wrong answer rather than an error.
    `DiscoveryEnvelopeTests` on the .NET side asserts exactly that, property by property, against a
    committed fixture.
    """
    return {
        "eventId": event_id or str(uuid.uuid4()),
        "occurredAt": (occurred_at or datetime.now(UTC)).isoformat(),
        "discoveryName": discovery_name,
        "scanProfileId": outcome.profile_id,
        "scanProfileName": outcome.profile_name,
        "scanId": outcome.scan_id,
        "address": device.address,
        "hostname": device.hostname,
        "hostnameSource": device.hostname_source,
        "respondedToPing": device.responded_to_ping,
        "openPorts": list(device.open_ports),
        "snmp": _identity(device.identity),
        "neighbours": [_neighbour(item) for item in device.neighbours],
    }


def _identity(identity: SnmpIdentity | None) -> dict[str, Any] | None:
    """
    The identity, or null for a device that answered no community.

    Null rather than an empty object, because the two mean different things downstream: WP-4.2
    matches a discovery to a CI on what it knows, and an identity present but blank would look like
    a device that identified itself as nothing.
    """
    if identity is None:
        return None
    return {
        "sysName": identity.sys_name,
        "sysDescription": identity.sys_description,
        "sysObjectId": identity.sys_object_id,
        "sysLocation": identity.sys_location,
        "sysContact": identity.sys_contact,
        "uptimeSeconds": identity.uptime_seconds,
        # Deliberately not the community that answered. The scanner knows it, logs it, and uses it
        # for the neighbour walk — but it is the one thing a scan learns that is a secret in a real
        # estate, and an event that carried it would put a credential on the bus and into a review
        # queue.
    }


def _neighbour(neighbour: Neighbour) -> dict[str, Any]:
    return {
        "protocol": neighbour.protocol,
        "localPort": neighbour.local_port,
        "remoteSystemName": neighbour.remote_system_name,
        "remotePort": neighbour.remote_port,
        "remoteAddress": neighbour.remote_address,
    }
