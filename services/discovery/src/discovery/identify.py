"""Asking a device that answered what it is, and who it is plugged into."""

from __future__ import annotations

import logging
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field

from . import oids as oid
from .snmp import DEFAULT_PORT, SnmpError, SnmpTarget, SnmpTransport, SnmpValue

logger = logging.getLogger("discovery.identify")

LLDP = "lldp"
CDP = "cdp"


@dataclass(frozen=True, slots=True)
class Neighbour:
    """One link a device reported, as the device described it."""

    protocol: str
    local_port: str | None = None
    remote_system_name: str | None = None
    remote_port: str | None = None
    remote_address: str | None = None


@dataclass(frozen=True, slots=True)
class SnmpIdentity:
    """
    What a device said about itself, and which community it said it on.

    Every field is optional because every field genuinely is: an agent that answers sysDescr and
    nothing else is common, and discarding the identity because sysLocation was never configured
    would throw away the one fact that tells a router from a printer.
    """

    community: str
    sys_name: str | None = None
    sys_description: str | None = None
    sys_object_id: str | None = None
    sys_location: str | None = None
    sys_contact: str | None = None
    uptime_seconds: float | None = None
    neighbours: tuple[Neighbour, ...] = field(default_factory=tuple)


async def identify(
    address: str,
    communities: Sequence[str],
    transport: SnmpTransport,
    timeout_seconds: float = 2.0,
    port: int = DEFAULT_PORT,
) -> SnmpIdentity | None:
    """
    Tries each community in order and returns the first identity that comes back.

    Ordered rather than concurrent on purpose: the list is short, the right one is usually first,
    and firing every community at a stranger simultaneously is what an SNMP brute-force looks like
    in an IDS log. A device that answers none of them is not an error — most addresses in a range
    have no agent — so this returns None and the discovery still reports the ping and the open
    ports.
    """
    for community in communities:
        target = SnmpTarget(
            host=address, community=community, port=port, timeout_seconds=timeout_seconds)
        try:
            values = await transport.get(target, [
                oid.SYS_DESCR, oid.SYS_NAME, oid.SYS_LOCATION, oid.SYS_CONTACT,
                oid.SYS_OBJECT_ID, oid.SYS_UPTIME,
            ])
        except SnmpError:
            continue
        except Exception:
            # One address's failure, never the scan's. A malformed answer from one agent must not
            # stop the sweep that found it.
            logger.exception("SNMP identify failed.", extra={"address": address})
            continue

        identity = _read_identity(community, values)
        if identity is not None:
            return identity

    return None


async def walk_neighbours(
    address: str,
    community: str,
    transport: SnmpTransport,
    timeout_seconds: float = 2.0,
    port: int = DEFAULT_PORT,
) -> tuple[Neighbour, ...]:
    """
    LLDP first, then CDP, and both are additive.

    A Cisco switch with LLDP switched on reports the same link twice, once per protocol, and the
    two are kept as two: they carry different fields — CDP advertises a management address, LLDP
    does not without a second indexed table — and deciding they are one link is topology work,
    which is WP-4.3's.

    Every walk is guarded on its own. A device with no LLDP MIB answers the walk with nothing,
    which is indistinguishable from a device with no neighbours and is treated the same way.
    """
    target = SnmpTarget(
        host=address, community=community, port=port, timeout_seconds=timeout_seconds)

    neighbours: list[Neighbour] = []
    neighbours.extend(await _walk_lldp(target, transport, address))
    neighbours.extend(await _walk_cdp(target, transport, address))
    return tuple(neighbours)


async def _walk_lldp(
    target: SnmpTarget,
    transport: SnmpTransport,
    address: str,
) -> list[Neighbour]:
    try:
        names = await transport.walk(target, oid.LLDP_REM_SYS_NAME)
        ports = await transport.walk(target, oid.LLDP_REM_PORT_ID)
        chassis = await transport.walk(target, oid.LLDP_REM_CHASSIS_ID)
        local_ports = await transport.walk(target, oid.LLDP_LOC_PORT_ID) if names or chassis else {}
    except SnmpError:
        return []
    except Exception:
        logger.exception("LLDP walk failed.", extra={"address": address})
        return []

    return [
        Neighbour(
            protocol=LLDP,
            local_port=local_ports.get(_lldp_local_port(index)),
            # A neighbour that advertised no system name still gets a row: its chassis id — a MAC
            # address, usually — is what makes the link identifiable at all, and dropping the row
            # would hide a cable somebody has to trace.
            remote_system_name=_text(names.get(index)) or _text(chassis.get(index)),
            remote_port=_text(ports.get(index)),
        )
        # Keyed on the union, so a table that answers chassis ids and no names still produces rows.
        for index in sorted(set(names) | set(chassis))
    ]


async def _walk_cdp(
    target: SnmpTarget,
    transport: SnmpTransport,
    address: str,
) -> list[Neighbour]:
    try:
        devices = await transport.walk(target, oid.CDP_CACHE_DEVICE_ID)
        if not devices:
            # Skip three more walks against a device with no CDP cache, which is most of them.
            return []
        ports = await transport.walk(target, oid.CDP_CACHE_DEVICE_PORT)
        addresses = await transport.walk(target, oid.CDP_CACHE_ADDRESS)
        interfaces = await transport.walk(target, oid.IF_NAME)
    except SnmpError:
        return []
    except Exception:
        logger.exception("CDP walk failed.", extra={"address": address})
        return []

    return [
        Neighbour(
            protocol=CDP,
            local_port=interfaces.get(_cdp_if_index(index)),
            remote_system_name=_text(devices.get(index)),
            remote_port=_text(ports.get(index)),
            remote_address=_decode_address(addresses.get(index)),
        )
        for index in sorted(devices)
    ]


def _read_identity(community: str, values: Mapping[str, SnmpValue]) -> SnmpIdentity | None:
    """
    Builds the identity, or None when the agent answered without saying anything.

    An agent that returns every scalar empty has answered the socket without identifying itself,
    and reporting that as an identity would put a device with no name and no description into a
    review queue as though the scan had learned something.
    """
    uptime = _uptime(values.get(oid.SYS_UPTIME))
    identity = SnmpIdentity(
        community=community,
        sys_name=_text(values.get(oid.SYS_NAME)),
        sys_description=_text(values.get(oid.SYS_DESCR)),
        sys_object_id=_text(values.get(oid.SYS_OBJECT_ID)),
        sys_location=_text(values.get(oid.SYS_LOCATION)),
        sys_contact=_text(values.get(oid.SYS_CONTACT)),
        uptime_seconds=uptime,
    )

    if (identity.sys_name or identity.sys_description or identity.sys_object_id
            or identity.sys_location or identity.sys_contact or uptime is not None):
        return identity
    return None


def _lldp_local_port(index: str) -> str:
    """
    The local port number out of an `lldpRemTable` index of `timeMark.localPortNum.remIndex`.

    Reading it out of the index is why `lldpRemLocalPortNum` is never walked: the table is indexed
    by the port, so the walk already carries it, and a column read would be a fourth round trip to
    learn something already in hand.
    """
    parts = index.split(".")
    return parts[1] if len(parts) >= 2 else index


def _cdp_if_index(index: str) -> str:
    """The ifIndex out of a `cdpCacheTable` index of `ifIndex.deviceIndex`."""
    return index.split(".")[0]


def _text(value: SnmpValue | None) -> str | None:
    if value is None:
        return None
    trimmed = str(value).strip()
    return trimmed or None


def _uptime(value: SnmpValue | None) -> float | None:
    if value is None:
        return None
    try:
        return float(value) / oid.TIMETICKS_PER_SECOND
    except (TypeError, ValueError):
        return None


def _decode_address(value: SnmpValue | None) -> str | None:
    """
    `cdpCacheAddress` is an octet string, so pysnmp renders four bytes of IPv4 as `0x0a000001`.

    Decoded here rather than left as hex because the whole value of the field is that somebody can
    read it, and an IPv6 or NSAP address — legal in this column and a different length — is left
    alone rather than mangled into a wrong dotted quad.
    """
    text = _text(value)
    if text is None:
        return None
    if text.startswith("0x") and len(text) == 10:
        try:
            octets = bytes.fromhex(text[2:])
        except ValueError:
            return text
        return ".".join(str(octet) for octet in octets)
    return text
