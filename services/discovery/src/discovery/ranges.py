"""Turning a range as an operator wrote it into the addresses a sweep will probe."""

from __future__ import annotations

import ipaddress
import logging
import socket
from collections.abc import Callable, Iterable

logger = logging.getLogger("discovery.ranges")

#: The subnet this scanner is attached to, resolved here rather than configured.
LOCAL_KEYWORD = "local"

#: How wide a `local` sweep is allowed to get.
#:
#: Docker hands a user-defined network a /16, so the interface inside an Aspire session genuinely
#: reports one — and a /16 is 65,534 probes, which is minutes of sweeping to find the six
#: containers sitting in the first /24 of it. `local` therefore means "at most the /24 containing
#: this scanner's own address", and the narrowing is logged with both numbers so nobody has to
#: guess which was scanned. A wider sweep is still available: write the CIDR out and own the
#: decision.
LOCAL_MAX_PREFIX = 24

#: Where Linux publishes the routing table. Read rather than ioctl'd because the netmask is right
#: there in it — `SIOCGIFNETMASK` needs an interface name this service would have to find first.
ROUTE_TABLE_PATH = "/proc/net/route"

#: An address in TEST-NET-1: routed nowhere, so connecting a datagram socket to it sends no packet
#: while still making the kernel pick the source address it would have used.
ROUTE_PROBE_ADDRESS = "192.0.2.1"

#: The same ceiling the API enforces (`ScanRange.MaximumAddressesPerRange`). Checked again here
#: because a profile could have been written before the limit existed, and a scanner that expanded
#: a /8 would allocate sixteen million strings before its first ping.
MAXIMUM_ADDRESSES_PER_RANGE = 65_536


class RangeError(ValueError):
    """A range string this scanner cannot expand. Reported per profile; never fatal."""


LocalResolver = Callable[[], ipaddress.IPv4Network]


def expand(text: str, local: LocalResolver | None = None) -> list[str]:
    """
    Expands one range into the addresses to probe, in order.

    Four forms, mirroring `ScanRange` in `Modules.Monitoring` exactly — including that a block
    wider than a /31 omits its network and broadcast addresses, so the counts on both sides agree:

    - `local` — the subnet this scanner sits on, narrowed to :data:`LOCAL_MAX_PREFIX`
    - `10.0.0.0/24` — a CIDR block
    - `10.0.0.5-40` or `10.0.0.5-10.0.0.40` — an inclusive span inside one /24
    - `10.0.0.5` — one address

    The API validates all four at the edge, so reaching an error here means a row was written
    behind it or the two implementations have drifted. Either way it is one profile's problem, not
    the cycle's.
    """
    raw = text.strip()
    if not raw:
        raise RangeError("A range cannot be empty.")

    if raw.casefold() == LOCAL_KEYWORD:
        resolver = local if local is not None else local_subnet
        return _from_network(resolver())

    if "/" in raw:
        try:
            network = ipaddress.IPv4Network(raw, strict=False)
        except ValueError as error:
            raise RangeError(f"'{raw}' is not an IPv4 CIDR block: {error}") from error
        return _from_network(network)

    if "-" in raw:
        return _from_span(raw)

    return [str(_address(raw))]


def expand_all(ranges: Iterable[str], local: LocalResolver | None = None) -> list[str]:
    """
    Expands every range on a profile, dropping duplicates while keeping the order they were written
    in.

    Deduplicating matters: two ranges that overlap would otherwise probe the same address twice and
    publish it twice, which reads downstream as two devices at one address.
    """
    seen: set[str] = set()
    addresses: list[str] = []
    for text in ranges:
        for address in expand(text, local):
            if address not in seen:
                seen.add(address)
                addresses.append(address)
    return addresses


def _from_network(network: ipaddress.IPv4Network) -> list[str]:
    if network.num_addresses > MAXIMUM_ADDRESSES_PER_RANGE:
        raise RangeError(
            f"'{network}' is {network.num_addresses} addresses, which is above the limit of "
            f"{MAXIMUM_ADDRESSES_PER_RANGE}.")

    # A /31 is a point-to-point link and a /32 is one host: every value in the block is a host.
    # Wider blocks reserve the first and last, and probing them finds nothing.
    if network.prefixlen >= 31:
        return [str(address) for address in network]
    return [str(address) for address in network.hosts()]


def _from_span(raw: str) -> list[str]:
    start_text, _, end_text = raw.partition("-")
    start = _address(start_text)
    octets = start.packed

    if "." in end_text:
        end = _address(end_text)
        if end.packed[:3] != octets[:3]:
            raise RangeError(
                f"'{raw}' spans more than one /24, which a range of this form cannot express.")
        last = end.packed[3]
    else:
        try:
            last = int(end_text.strip())
        except ValueError as error:
            raise RangeError(f"'{end_text.strip()}' is not a final octet.") from error
        if not 0 <= last <= 255:
            raise RangeError(f"'{end_text.strip()}' is not a final octet between 0 and 255.")

    if last < octets[3]:
        raise RangeError(f"'{raw}' ends before it starts.")

    prefix = ".".join(str(octet) for octet in octets[:3])
    return [f"{prefix}.{octet}" for octet in range(octets[3], last + 1)]


def _address(text: str) -> ipaddress.IPv4Address:
    trimmed = text.strip()
    # Dotted-quad only. `IPv4Address("10")` is a valid call that answers 0.0.0.10, which would turn
    # a typo into a range nobody meant — the same reason the C# side refuses anything but four
    # octets.
    if trimmed.count(".") != 3:
        raise RangeError(f"'{trimmed}' is not a dotted-quad IPv4 address.")
    try:
        return ipaddress.IPv4Address(trimmed)
    except ValueError as error:
        raise RangeError(f"'{trimmed}' is not an IPv4 address: {error}") from error


def local_subnet(
    route_table: str | None = None,
    own_address: str | None = None,
) -> ipaddress.IPv4Network:
    """
    The subnet this scanner is on, narrowed to :data:`LOCAL_MAX_PREFIX`.

    Both inputs are injectable so the whole resolution is testable without a network: in production
    they come from :data:`ROUTE_TABLE_PATH` and from the source address the kernel picks for an
    unroutable destination.
    """
    address = _address(own_address if own_address is not None else _own_address())
    routes = parse_routes(route_table if route_table is not None else _read_route_table())
    network = choose_route(routes, address)
    narrowed = narrow(network, address)
    if narrowed != network:
        logger.info(
            "Narrowed the local sweep.",
            extra={"interface_network": str(network), "scanning": str(narrowed)},
        )
    return narrowed


def parse_routes(route_table: str) -> list[ipaddress.IPv4Network]:
    """
    The directly connected networks in Linux's routing table, in the order they appear.

    A connected route is one with no gateway and a destination that is not the default route. Both
    columns are little-endian hex, which is why this is a parser rather than an int() call.
    """
    networks: list[ipaddress.IPv4Network] = []
    for line in route_table.splitlines()[1:]:
        columns = line.split()
        if len(columns) < 8:
            continue
        destination, gateway, mask = columns[1], columns[2], columns[7]
        if not _is_zero(gateway) or _is_zero(destination):
            continue
        try:
            # The netmask goes in as text: the two-element form takes an address and a *string*
            # mask (or a prefix length), and handing it an IPv4Address raises from inside
            # ipaddress.
            network = ipaddress.IPv4Network(
                (_from_hex(destination), str(_from_hex(mask))), strict=False)
        except ValueError:
            continue
        if network.is_loopback:
            continue
        networks.append(network)
    return networks


def choose_route(
    networks: Iterable[ipaddress.IPv4Network],
    address: ipaddress.IPv4Address,
) -> ipaddress.IPv4Network:
    """
    The connected network this scanner's own address sits in.

    Matching on the address rather than taking the first route matters on a host with several
    interfaces: the useful subnet is the one this process would be reached on, and "the first line
    of the routing table" is a docker bridge as often as not.
    """
    candidates = list(networks)
    for network in candidates:
        if address in network:
            return network

    if candidates:
        # An address that is in none of them means the routing table and the source address
        # disagree, which is worth saying rather than guessing about.
        raise RangeError(
            f"'{address}' is not inside any connected network ({', '.join(str(n) for n in
            candidates)}).")
    raise RangeError("No connected network was found in the routing table.")


def narrow(
    network: ipaddress.IPv4Network,
    address: ipaddress.IPv4Address,
    max_prefix: int = LOCAL_MAX_PREFIX,
) -> ipaddress.IPv4Network:
    """The block at `max_prefix` inside `network` that holds `address`, or `network` if narrower."""
    if network.prefixlen >= max_prefix:
        return network
    return ipaddress.IPv4Network(f"{address}/{max_prefix}", strict=False)


def _own_address() -> str:
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        # Connecting a datagram socket sends nothing; it only makes the kernel choose a route and
        # therefore a source address. It works on a host with no default route to the destination.
        probe.connect((ROUTE_PROBE_ADDRESS, 9))
        return str(probe.getsockname()[0])


def _read_route_table() -> str:
    with open(ROUTE_TABLE_PATH, encoding="utf-8") as handle:
        return handle.read()


def _is_zero(hex_text: str) -> bool:
    return int(hex_text, 16) == 0


def _from_hex(hex_text: str) -> ipaddress.IPv4Address:
    """Little-endian hex, as `/proc/net/route` writes it: `0011A8C0` is 192.168.17.0."""
    value = int(hex_text, 16)
    return ipaddress.IPv4Address(value.to_bytes(4, "little"))
