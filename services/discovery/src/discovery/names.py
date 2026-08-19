"""
Naming an address when reverse DNS will not.

A home or small-office LAN usually has no PTR records at all, so `getnameinfo` answers nothing for
every real device on it and a review queue fills with bare IPv4 addresses. Two protocols still
answer on such a network, and between them they cover most of what is plugged into one:

* **mDNS** (UDP 5353) — Apple devices, printers, Chromecasts, smart TVs, modern Linux, Windows 10+.
* **NetBIOS name service** (UDP 137) — Windows machines, Samba shares, most NAS boxes.

Both are spoken here directly rather than through a library, because neither needs more than a
packet out and a packet parsed, and SESSION.md §3.2 forbids adding a dependency unasked. Both are
strictly best-effort: every failure path returns None, because "no name" is the ordinary answer for
most addresses in a range and is never worth failing a scan over.
"""

from __future__ import annotations

import asyncio
import logging
import random
import socket
import string
from dataclasses import dataclass
from ipaddress import IPv4Address

logger = logging.getLogger("discovery.names")

#: Where a name came from, carried onto the discovery so an approver can weigh it. A PTR record and
#: a NetBIOS answer are not equally trustworthy, and the card says which one it is showing.
SOURCE_DNS = "dns"
SOURCE_MDNS = "mdns"
SOURCE_NETBIOS = "netbios"

MDNS_GROUP = "224.0.0.251"
MDNS_PORT = 5353
NETBIOS_PORT = 137

#: Both protocols are one datagram out and one back on a local network. A device that has not
#: answered in a second is not going to, and a sweep waits for this once per unnamed address.
DEFAULT_TIMEOUT_SECONDS = 1.0

#: NetBIOS names are 16 bytes; the 16th is the suffix that says what kind of name it is. 0x00 on a
#: unique (non-group) name is the workstation name, which is the machine name a person would say.
NETBIOS_WORKSTATION_SUFFIX = 0x00

#: Printable characters a NetBIOS name may contain. Anything else means the packet was not what it
#: claimed to be, and a control character in a hostname would travel into a CMDB and a UI.
_NAME_CHARACTERS = frozenset(string.ascii_letters + string.digits + "-_.$ ")


@dataclass(frozen=True, slots=True)
class ResolvedName:
    """A name and the protocol that produced it."""

    name: str
    source: str


async def resolve_name(
    address: str,
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
) -> ResolvedName | None:
    """
    Asks mDNS and NetBIOS at once, and takes whichever answers.

    Concurrently rather than in order: they are different protocols to different ports and a device
    answers at most one of them, so asking in sequence would spend the first timeout finding that
    out. mDNS wins a tie — it is the name a device chose to advertise, while a NetBIOS name is
    frequently a truncated, upper-cased relic.

    Reverse DNS is *not* attempted here. The sweep already did it, and this is only reached for the
    addresses it could not name.
    """
    results = await asyncio.gather(
        resolve_mdns(address, timeout_seconds),
        resolve_netbios(address, timeout_seconds),
        return_exceptions=True,
    )

    for candidate, source in zip(results, (SOURCE_MDNS, SOURCE_NETBIOS), strict=True):
        if isinstance(candidate, BaseException) or candidate is None:
            continue
        return ResolvedName(candidate, source)
    return None


async def resolve_mdns(
    address: str,
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
) -> str | None:
    """
    A reverse lookup over multicast DNS: PTR for `<reversed>.in-addr.arpa`.

    Ordinary DNS wire format on a multicast address, so the query is built and the answer parsed
    with the same routines a unicast resolver would use. The reply comes from the device itself
    rather than from a server, which is what makes it work on a network with no DNS infrastructure
    at all.
    """
    try:
        query_name = _reverse_pointer(address)
    except ValueError:
        return None

    query = _dns_query(query_name, qtype=12)
    reply = await _ask(MDNS_GROUP, MDNS_PORT, query, timeout_seconds, expect_from=address)
    if reply is None:
        return None

    name = _dns_first_pointer(reply)
    return _clean(name.removesuffix(".local").removesuffix(".")) if name else None


async def resolve_netbios(
    address: str,
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
) -> str | None:
    """
    A NetBIOS node status request, which asks a machine to list the names it answers to.

    The wildcard name `*` is what makes this work without knowing anything about the host first.
    Of the names that come back, the unique workstation name (suffix 0x00, not a group) is the one
    a person would recognise as the machine's name.
    """
    query = _netbios_status_query()
    reply = await _ask(address, NETBIOS_PORT, query, timeout_seconds)
    return _netbios_first_workstation(reply) if reply else None


async def _ask(
    host: str,
    port: int,
    payload: bytes,
    timeout_seconds: float,
    expect_from: str | None = None,
) -> bytes | None:
    """
    One datagram out, the first useful one back, or None.

    `expect_from` exists for the multicast case: a query to 224.0.0.251 is heard by every device on
    the segment and any of them may answer, so a reply is only interesting if it came from the
    address being asked about. Without that check, one chatty device would name the whole subnet
    after itself.
    """
    loop = asyncio.get_running_loop()
    received: asyncio.Future[bytes] = loop.create_future()

    class _Protocol(asyncio.DatagramProtocol):
        def datagram_received(self, data: bytes, addr: tuple[str | int, ...]) -> None:
            if received.done():
                return
            if expect_from is not None and str(addr[0]) != expect_from:
                return
            received.set_result(data)

        def error_received(self, exc: Exception) -> None:
            if not received.done():
                received.set_exception(exc)

    transport = None
    try:
        transport, _ = await loop.create_datagram_endpoint(
            _Protocol,
            # Bound to any port on IPv4. No reuse and no membership: this sends a multicast query
            # and reads the unicast-or-multicast reply, rather than joining the group as a listener.
            local_addr=("0.0.0.0", 0),
            family=socket.AF_INET,
        )
        transport.sendto(payload, (host, port))
        return await asyncio.wait_for(received, timeout_seconds)
    except (TimeoutError, OSError, asyncio.CancelledError):
        # Every one of these is the ordinary answer for an address that is not there, or a network
        # that will not carry multicast. None of them is worth a log line per address.
        return None
    except Exception:
        logger.debug("Name lookup failed for %s:%s.", host, port, exc_info=True)
        return None
    finally:
        if transport is not None:
            transport.close()


def _reverse_pointer(address: str) -> str:
    """`192.168.1.9` → `9.1.168.192.in-addr.arpa`. Raises for anything that is not IPv4."""
    return IPv4Address(address).reverse_pointer


def _dns_query(name: str, qtype: int) -> bytes:
    """A standard DNS query, one question, recursion off — nothing here talks to a resolver."""
    header = bytes([
        *random.randbytes(2),   # transaction id
        0x00, 0x00,             # flags: standard query
        0x00, 0x01,             # one question
        0x00, 0x00,             # no answers
        0x00, 0x00,             # no authority records
        0x00, 0x00,             # no additional records
    ])
    return header + _dns_encode_name(name) + qtype.to_bytes(2, "big") + b"\x00\x01"


def _dns_encode_name(name: str) -> bytes:
    encoded = bytearray()
    for label in name.rstrip(".").split("."):
        raw = label.encode("ascii", errors="ignore")[:63]
        encoded.append(len(raw))
        encoded += raw
    encoded.append(0)
    return bytes(encoded)


def _dns_first_pointer(message: bytes) -> str | None:
    """
    The first PTR answer's target, or None.

    Only the answer section is read, and only far enough to find one usable name. A device's mDNS
    reply may also carry the addresses and services it advertises; none of that is wanted here.
    """
    try:
        answers = int.from_bytes(message[6:8], "big")
        if answers == 0:
            return None

        offset = 12
        questions = int.from_bytes(message[4:6], "big")
        for _ in range(questions):
            offset = _dns_skip_name(message, offset) + 4

        for _ in range(answers):
            offset = _dns_skip_name(message, offset)
            record_type = int.from_bytes(message[offset:offset + 2], "big")
            length = int.from_bytes(message[offset + 8:offset + 10], "big")
            data = offset + 10
            if record_type == 12:
                name, _ = _dns_read_name(message, data)
                return name
            offset = data + length
    except (IndexError, ValueError, RecursionError):
        # A malformed or truncated reply is just an address with no name.
        return None
    return None


def _dns_skip_name(message: bytes, offset: int) -> int:
    while True:
        length = message[offset]
        if length == 0:
            return offset + 1
        if length & 0xC0 == 0xC0:
            # A compression pointer is always the last thing in a name.
            return offset + 2
        offset += length + 1


def _dns_read_name(message: bytes, offset: int, depth: int = 0) -> tuple[str, int]:
    # Bounded, because a reply is allowed to point a name at itself and an unbounded reader would
    # follow that until the stack ran out.
    if depth > 10:
        raise ValueError("Too many compression pointers.")

    labels: list[str] = []
    while True:
        length = message[offset]
        if length == 0:
            return ".".join(labels), offset + 1
        if length & 0xC0 == 0xC0:
            target = int.from_bytes(message[offset:offset + 2], "big") & 0x3FFF
            suffix, _ = _dns_read_name(message, target, depth + 1)
            labels.append(suffix)
            return ".".join(labels), offset + 2
        labels.append(message[offset + 1:offset + 1 + length].decode("ascii", errors="ignore"))
        offset += 1 + length


def _netbios_status_query() -> bytes:
    """A node status request for the wildcard name, which every NetBIOS host answers."""
    header = bytes([
        *random.randbytes(2),   # transaction id
        0x00, 0x00,             # flags: query, no recursion
        0x00, 0x01,             # one question
        0x00, 0x00,
        0x00, 0x00,
        0x00, 0x00,
    ])
    # `*` padded to sixteen bytes with NULs, then first-level encoded: every byte becomes two
    # characters in the range A-P. It is the protocol's own encoding, not an obfuscation.
    name = b"*" + b"\x00" * 15
    encoded = bytearray([32])
    for byte in name:
        encoded.append(ord("A") + (byte >> 4))
        encoded.append(ord("A") + (byte & 0x0F))
    encoded.append(0)
    # NBSTAT (0x21) in class IN.
    return header + bytes(encoded) + b"\x00\x21\x00\x01"


def _netbios_first_workstation(message: bytes) -> str | None:
    """
    The unique workstation name from a node status reply.

    The reply carries a name count and then that many 18-byte entries: sixteen bytes of name, then
    two flag bytes whose top bit marks a group name. A group name is a workgroup or domain — the
    same value on every machine in it — so taking one would name six devices identically.
    """
    try:
        if int.from_bytes(message[6:8], "big") == 0:
            return None

        # Driven by the header's own counts rather than assuming the question is echoed back. Most
        # implementations do echo it, but a reply that does not would otherwise be read one section
        # out of step and produce a name made of the wrong bytes.
        offset = 12
        for _ in range(int.from_bytes(message[4:6], "big")):
            offset = _dns_skip_name(message, offset) + 4

        offset = _dns_skip_name(message, offset) + 10  # type, class, ttl, data length
        count = message[offset]
        offset += 1

        for _ in range(count):
            raw = message[offset:offset + 16]
            flags = int.from_bytes(message[offset + 16:offset + 18], "big")
            offset += 18
            if raw[15] != NETBIOS_WORKSTATION_SUFFIX or flags & 0x8000:
                continue
            if (name := _clean(raw[:15].decode("ascii", errors="ignore"))) is not None:
                return name
    except (IndexError, ValueError):
        return None
    return None


def _clean(name: str | None) -> str | None:
    """
    Trims and rejects anything that is not plausibly a hostname.

    A name from either of these protocols is written by whatever answered, travels the bus, lands in
    a review queue and can become a CI name. It is checked here rather than trusted.
    """
    if name is None:
        return None
    trimmed = name.strip().strip("\x00").strip()
    if not trimmed or len(trimmed) > 100:
        return None
    if not set(trimmed) <= _NAME_CHARACTERS:
        return None
    return trimmed
