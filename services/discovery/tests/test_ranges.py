from __future__ import annotations

import ipaddress

import pytest

from discovery.ranges import (
    LOCAL_MAX_PREFIX,
    RangeError,
    choose_route,
    expand,
    expand_all,
    local_subnet,
    narrow,
    parse_routes,
)

#: `/proc/net/route` as it reads inside a container on a Docker user-defined network: a default
#: route through the gateway, and one connected /16. Hex, little-endian, tab-separated.
ROUTE_TABLE = """Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT
eth0\t00000000\t010012AC\t0003\t0\t0\t0\t00000000\t0\t0\t0
eth0\t000012AC\t00000000\t0001\t0\t0\t0\t0000FFFF\t0\t0\t0
"""


def test_expand_cidr_omits_the_network_and_broadcast_addresses() -> None:
    addresses = expand("10.0.0.0/29")

    # Six hosts out of eight addresses, and the count has to agree with `ScanRange` on the C# side:
    # anything wider than a /31 loses its first and last.
    assert addresses == [f"10.0.0.{octet}" for octet in range(1, 7)]


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("10.0.0.4/31", ["10.0.0.4", "10.0.0.5"]),
        ("10.0.0.7/32", ["10.0.0.7"]),
    ],
)
def test_expand_a_point_to_point_or_single_host_block_keeps_every_address(
    text: str,
    expected: list[str],
) -> None:
    # A /31 is a link and a /32 is one host: there is no network or broadcast address to omit, and
    # omitting them would expand both to nothing.
    assert expand(text) == expected


def test_expand_a_span_is_inclusive_at_both_ends() -> None:
    assert expand("10.0.0.5-8") == ["10.0.0.5", "10.0.0.6", "10.0.0.7", "10.0.0.8"]


def test_expand_a_span_written_as_two_addresses_reads_the_same() -> None:
    assert expand("10.0.0.5-10.0.0.7") == ["10.0.0.5", "10.0.0.6", "10.0.0.7"]


def test_expand_a_single_address_is_one_probe() -> None:
    assert expand("192.0.2.1") == ["192.0.2.1"]


@pytest.mark.parametrize(
    ("text", "message"),
    [
        ("", "cannot be empty"),
        ("10", "dotted-quad"),
        ("10.0.0.0/33", "not an IPv4 CIDR"),
        ("10.0.0.9-4", "ends before it starts"),
        ("10.0.0.1-10.0.1.5", "more than one /24"),
        ("10.0.0.1-300", "between 0 and 255"),
        ("not-an-address", "dotted-quad"),
        ("10.0.0.0/8", "above the limit"),
    ],
)
def test_expand_refuses_what_it_cannot_scan_and_says_why(text: str, message: str) -> None:
    # The message is the point: a scanner reports it against the profile, and "invalid range" would
    # start a conversation rather than end one.
    with pytest.raises(RangeError, match=message):
        expand(text)


def test_expand_all_deduplicates_across_overlapping_ranges() -> None:
    # Two ranges that overlap must not probe an address twice, because a second probe publishes the
    # same address as a second device.
    assert expand_all(["10.0.0.1-3", "10.0.0.2-4"]) == [
        "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4"]


def test_parse_routes_reads_only_the_connected_networks() -> None:
    networks = parse_routes(ROUTE_TABLE)

    # The default route has a gateway and a zero destination, so it is not a subnet to scan.
    assert networks == [ipaddress.IPv4Network("172.18.0.0/16")]


def test_choose_route_picks_the_network_this_scanner_is_in() -> None:
    networks = [ipaddress.IPv4Network("10.9.0.0/24"), ipaddress.IPv4Network("172.18.0.0/16")]

    chosen = choose_route(networks, ipaddress.IPv4Address("172.18.0.5"))

    assert chosen == ipaddress.IPv4Network("172.18.0.0/16")


def test_choose_route_with_no_matching_network_says_so() -> None:
    with pytest.raises(RangeError, match="not inside any connected network"):
        choose_route([ipaddress.IPv4Network("10.9.0.0/24")], ipaddress.IPv4Address("172.18.0.5"))


def test_narrow_caps_a_wide_interface_at_the_local_prefix() -> None:
    narrowed = narrow(
        ipaddress.IPv4Network("172.18.0.0/16"), ipaddress.IPv4Address("172.18.4.9"))

    # Docker hands out /16s, which is 65,534 probes to find the six containers in one /24 of it.
    # The narrowing keeps the address that resolved it, so the block scanned is the one this
    # scanner is on.
    assert narrowed == ipaddress.IPv4Network("172.18.4.0/24")
    assert narrowed.prefixlen == LOCAL_MAX_PREFIX


def test_narrow_leaves_an_already_narrow_interface_alone() -> None:
    network = ipaddress.IPv4Network("10.9.0.0/28")

    assert narrow(network, ipaddress.IPv4Address("10.9.0.3")) == network


def test_local_subnet_resolves_from_the_route_table_and_the_source_address() -> None:
    resolved = local_subnet(route_table=ROUTE_TABLE, own_address="172.18.0.2")

    assert resolved == ipaddress.IPv4Network("172.18.0.0/24")


def test_expand_local_uses_the_injected_resolver() -> None:
    addresses = expand("local", local=lambda: ipaddress.IPv4Network("10.0.0.0/30"))

    assert addresses == ["10.0.0.1", "10.0.0.2"]


def test_local_subnet_with_no_connected_route_reports_rather_than_guesses() -> None:
    default_only = "\n".join(ROUTE_TABLE.splitlines()[:2]) + "\n"

    with pytest.raises(RangeError, match="No connected network"):
        local_subnet(route_table=default_only, own_address="172.18.0.2")
