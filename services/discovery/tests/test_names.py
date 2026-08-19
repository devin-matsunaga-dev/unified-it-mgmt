"""Parsing the two protocols that still answer on a LAN with no reverse DNS."""

from __future__ import annotations

from discovery.names import (
    SOURCE_MDNS,
    SOURCE_NETBIOS,
    ResolvedName,
    _clean,
    _dns_encode_name,
    _dns_first_pointer,
    _netbios_first_workstation,
    _netbios_status_query,
    _reverse_pointer,
    resolve_name,
)


def test_reverse_pointer_is_the_address_backwards_in_the_arpa_zone() -> None:
    assert _reverse_pointer("192.168.1.9") == "9.1.168.192.in-addr.arpa"


def test_reverse_pointer_rejects_anything_that_is_not_an_ipv4_address() -> None:
    import pytest

    with pytest.raises(ValueError):
        _reverse_pointer("not-an-address")


def _mdns_reply(name: str) -> bytes:
    """A minimal DNS response carrying one PTR answer, uncompressed."""
    header = bytes([0x00, 0x01, 0x84, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00])
    question = _dns_encode_name("9.1.168.192.in-addr.arpa") + b"\x00\x0c\x00\x01"
    target = _dns_encode_name(name)
    answer = (
        _dns_encode_name("9.1.168.192.in-addr.arpa")
        + b"\x00\x0c\x00\x01"          # PTR, IN
        + b"\x00\x00\x00\x78"          # ttl
        + len(target).to_bytes(2, "big")
        + target
    )
    return header + question + answer


def test_a_ptr_answer_is_read_out_of_an_mdns_reply() -> None:
    assert _dns_first_pointer(_mdns_reply("living-room-tv.local")) == "living-room-tv.local"


def test_a_reply_with_no_answers_names_nothing() -> None:
    header = bytes([0x00, 0x01, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])

    assert _dns_first_pointer(header) is None


def test_a_truncated_reply_is_no_name_rather_than_a_crash() -> None:
    # A scan meets whatever is on the wire, including a device answering nonsense.
    assert _dns_first_pointer(_mdns_reply("printer.local")[:20]) is None


def test_a_compression_pointer_that_points_at_itself_terminates() -> None:
    header = bytes([0x00, 0x01, 0x84, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00])
    # An answer whose name is a pointer to itself: a malformed reply that an unbounded reader would
    # follow until the stack ran out.
    answer = b"\xc0\x0c" + b"\x00\x0c\x00\x01" + b"\x00\x00\x00\x78" + b"\x00\x02" + b"\xc0\x0c"

    assert _dns_first_pointer(header + answer) is None


def _netbios_reply(names: list[tuple[str, int, bool]]) -> bytes:
    header = bytes([0x00, 0x01, 0x84, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00])
    body = bytearray()
    body += b"\xc0\x0c" + b"\x00\x21\x00\x01" + b"\x00\x00\x00\x00"
    entries = bytearray([len(names)])
    for name, suffix, is_group in names:
        entries += name.ljust(15).encode("ascii")[:15]
        entries.append(suffix)
        entries += (0x8000 if is_group else 0x0400).to_bytes(2, "big")
    body += len(entries).to_bytes(2, "big") + entries
    return header + bytes(body)


def test_the_workstation_name_is_taken_from_a_node_status_reply() -> None:
    reply = _netbios_reply([("WORKGROUP", 0x00, True), ("DESKTOP-7F2K", 0x00, False)])

    # The group name is the workgroup — the same value on every machine in it, so taking one would
    # name six devices identically.
    assert _netbios_first_workstation(reply) == "DESKTOP-7F2K"


def test_a_reply_carrying_only_group_names_names_nothing() -> None:
    assert _netbios_first_workstation(_netbios_reply([("WORKGROUP", 0x00, True)])) is None


def test_a_service_suffix_is_not_the_machine_name() -> None:
    # 0x20 is the file-server service, not the workstation name.
    assert _netbios_first_workstation(_netbios_reply([("FILESERVER", 0x20, False)])) is None


def test_the_status_query_asks_for_the_wildcard_name() -> None:
    query = _netbios_status_query()

    # 32 bytes of first-level encoding, all in A-P, then NBSTAT in class IN.
    assert query[12] == 32
    assert set(query[13:45]) <= set(range(ord("A"), ord("P") + 1))
    assert query[-4:] == b"\x00\x21\x00\x01"


def test_a_name_with_control_characters_is_refused() -> None:
    # It would travel the bus, land in a review queue and can become a CI name.
    assert _clean("bad\x07name") is None


def test_a_name_is_trimmed_of_the_padding_the_protocols_use() -> None:
    assert _clean("DESKTOP-7F2K   ") == "DESKTOP-7F2K"


def test_an_absurdly_long_name_is_refused() -> None:
    assert _clean("a" * 101) is None


async def test_resolve_name_prefers_mdns_when_both_answer(monkeypatch) -> None:  # type: ignore[no-untyped-def]
    import discovery.names as names

    async def mdns(address: str, timeout_seconds: float = 1.0) -> str | None:
        return "living-room-tv"

    async def netbios(address: str, timeout_seconds: float = 1.0) -> str | None:
        return "LIVINGROOMTV"

    monkeypatch.setattr(names, "resolve_mdns", mdns)
    monkeypatch.setattr(names, "resolve_netbios", netbios)

    # mDNS is the name a device chose to advertise; a NetBIOS name is often a truncated relic.
    assert await resolve_name("192.168.1.9") == ResolvedName("living-room-tv", SOURCE_MDNS)


async def test_resolve_name_falls_back_to_netbios(monkeypatch) -> None:  # type: ignore[no-untyped-def]
    import discovery.names as names

    async def mdns(address: str, timeout_seconds: float = 1.0) -> str | None:
        return None

    async def netbios(address: str, timeout_seconds: float = 1.0) -> str | None:
        return "DESKTOP-7F2K"

    monkeypatch.setattr(names, "resolve_mdns", mdns)
    monkeypatch.setattr(names, "resolve_netbios", netbios)

    assert await resolve_name("192.168.1.9") == ResolvedName("DESKTOP-7F2K", SOURCE_NETBIOS)


async def test_resolve_name_when_one_protocol_raises_still_uses_the_other(monkeypatch) -> None:  # type: ignore[no-untyped-def]
    import discovery.names as names

    async def mdns(address: str, timeout_seconds: float = 1.0) -> str | None:
        raise OSError("no multicast route")

    async def netbios(address: str, timeout_seconds: float = 1.0) -> str | None:
        return "DESKTOP-7F2K"

    monkeypatch.setattr(names, "resolve_mdns", mdns)
    monkeypatch.setattr(names, "resolve_netbios", netbios)

    # A network that will not carry multicast is ordinary, not a failure.
    assert await resolve_name("192.168.1.9") == ResolvedName("DESKTOP-7F2K", SOURCE_NETBIOS)
