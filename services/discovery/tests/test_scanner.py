from __future__ import annotations

import ipaddress
from collections.abc import Sequence

from discovery.config import ScanProfile
from discovery.identify import LLDP
from discovery.names import ResolvedName
from discovery.scanner import Scanner
from discovery.snmp import SnmpTarget, SnmpValue
from discovery.sweep import NetworkSweep, ProbeObserver, SweepResult

from .test_identify import SYS_INFO, FakeTransport


class RecordingSweep(NetworkSweep):
    """Stands in for the network: answers with fixed results, recording what it was asked."""

    def __init__(self, found: Sequence[SweepResult]) -> None:
        super().__init__()
        self._found = list(found)
        self.addresses: list[str] = []
        self.ports: list[int] = []

    async def run(
        self,
        addresses: Sequence[str],
        ports: Sequence[int],
        timeout_seconds: float,
        on_probe: ProbeObserver | None = None,
    ) -> list[SweepResult]:
        self.addresses = list(addresses)
        self.ports = list(ports)
        found = [result for result in self._found if result.address in self.addresses]
        if on_probe is not None:
            answered = {result.address for result in found}
            for address in self.addresses:
                on_probe(address, address in answered)
        return found


async def _no_name(address: str) -> ResolvedName | None:
    """The default for these tests: reverse DNS found nothing and nothing else answers either.

    Explicit rather than left to the real resolver, which would put a multicast query and a
    one-second timeout per unnamed address into every test in this file."""
    return None


def build_scanner(sweep: NetworkSweep, **overrides: object) -> Scanner:
    return Scanner(sweep, name_resolver=_no_name, **overrides)  # type: ignore[arg-type]


def profile(**overrides: object) -> ScanProfile:
    defaults: dict[str, object] = {
        "profile_id": "0199c0de-4100-7000-8000-000000000001",
        "name": "Local subnet sweep",
        "ranges": ("10.0.0.0/29",),
        "ports": (22,),
        "interval_seconds": 300,
        "timeout_seconds": 2.0,
        "snmp_enabled": True,
        "neighbour_discovery_enabled": True,
    }
    return ScanProfile(**(defaults | overrides))  # type: ignore[arg-type]


def alive(address: str, ports: tuple[int, ...] = ()) -> SweepResult:
    return SweepResult(
        address=address, responded_to_ping=True, latency_ms=0.5, open_ports=ports, hostname=None)


async def test_scan_expands_the_range_and_probes_every_address_in_it() -> None:
    sweep = RecordingSweep([])

    outcome = await build_scanner(sweep).scan(profile())

    assert sweep.addresses == [f"10.0.0.{octet}" for octet in range(1, 7)]
    assert sweep.ports == [22]
    assert outcome.addresses_probed == 6
    assert outcome.devices == ()


async def test_scan_identifies_what_answered_and_walks_its_neighbours() -> None:
    transport = FakeTransport(
        community="healthy",
        walks={
            "1.0.8802.1.1.2.1.4.1.1.9": {"0.1.1": "dc1-core-rtr-01"},
            "1.0.8802.1.1.2.1.3.7.1.3": {"1": "GigabitEthernet0/1"},
        },
    )
    scanner = build_scanner(
        RecordingSweep([alive("10.0.0.2", ports=(22,))]),
        transport=transport,
        communities=["healthy"],
    )

    outcome = await scanner.scan(profile())

    assert len(outcome.devices) == 1
    device = outcome.devices[0]
    assert device.address == "10.0.0.2"
    assert device.open_ports == (22,)
    assert device.identity is not None
    assert device.identity.sys_name == "sim-switch-healthy"
    assert [item.protocol for item in device.neighbours] == [LLDP]
    assert device.neighbours[0].remote_system_name == "dc1-core-rtr-01"


async def test_scan_walks_neighbours_with_the_community_that_already_worked() -> None:
    transport = FakeTransport(community="degraded", walks={})
    scanner = build_scanner(
        RecordingSweep([alive("10.0.0.2")]),
        transport=transport,
        communities=["healthy", "degraded"],
    )

    await scanner.scan(profile())

    # The identify has just established which community this device answers on. Trying the list
    # again for the walk would be a failed authentication per device, per walk.
    assert transport.communities_tried == ["healthy", "degraded"]


async def test_scan_with_snmp_disabled_reports_the_sweep_and_asks_nothing() -> None:
    transport = FakeTransport(community="healthy")
    scanner = build_scanner(
        RecordingSweep([alive("10.0.0.2", ports=(22,))]),
        transport=transport,
        communities=["healthy"],
    )

    outcome = await scanner.scan(profile(snmp_enabled=False))

    assert len(outcome.devices) == 1
    assert outcome.devices[0].identity is None
    assert outcome.devices[0].open_ports == (22,)
    assert transport.communities_tried == []


async def test_scan_with_neighbour_discovery_disabled_still_identifies() -> None:
    transport = FakeTransport(community="healthy")
    scanner = build_scanner(
        RecordingSweep([alive("10.0.0.2")]),
        transport=transport,
        communities=["healthy"],
    )

    outcome = await scanner.scan(profile(neighbour_discovery_enabled=False))

    assert outcome.devices[0].identity is not None
    # The neighbour tables are the expensive half — two walks per device — and an estate of servers
    # has nothing in them.
    assert transport.roots_walked == []
    assert outcome.devices[0].neighbours == ()


async def test_scan_an_empty_range_is_a_clean_result_rather_than_a_failure() -> None:
    sweep = RecordingSweep([])

    outcome = await build_scanner(sweep).scan(profile(ranges=("192.0.2.0/29",)))

    # The WP's second verification case. Six addresses probed, nothing found, no crash — and the
    # probed count is what tells that apart from a profile whose ranges never expanded.
    assert outcome.addresses_probed == 6
    assert outcome.devices == ()
    assert outcome.range_errors == ()


async def test_scan_a_malformed_range_does_not_lose_the_ones_that_work() -> None:
    sweep = RecordingSweep([alive("10.0.0.1")])

    outcome = await build_scanner(sweep).scan(profile(ranges=("10.0.0.0/29", "nonsense")))

    assert sweep.addresses == [f"10.0.0.{octet}" for octet in range(1, 7)]
    assert len(outcome.range_errors) == 1
    assert "nonsense" in outcome.range_errors[0]
    assert len(outcome.devices) == 1


async def test_scan_with_every_range_malformed_probes_nothing_and_says_why() -> None:
    sweep = RecordingSweep([])

    outcome = await build_scanner(sweep).scan(profile(ranges=("nonsense", "10.0.0.0/8")))

    # "Nothing was looked at" has to be distinguishable from "nothing is there", or a profile with
    # a typo in it reads for weeks as an empty network.
    assert outcome.addresses_probed == 0
    assert len(outcome.range_errors) == 2
    assert sweep.addresses == []


async def test_scan_deduplicates_addresses_across_overlapping_ranges() -> None:
    sweep = RecordingSweep([])

    await build_scanner(sweep).scan(profile(ranges=("10.0.0.1-3", "10.0.0.2-4")))

    assert sweep.addresses == ["10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4"]


async def test_scan_resolves_the_local_keyword_through_the_injected_resolver() -> None:
    sweep = RecordingSweep([])
    scanner = build_scanner(sweep, local=lambda: ipaddress.IPv4Network("10.9.9.0/30"))

    await scanner.scan(profile(ranges=("local",)))

    assert sweep.addresses == ["10.9.9.1", "10.9.9.2"]


async def test_scan_gives_every_pass_its_own_scan_id() -> None:
    scanner = build_scanner(RecordingSweep([alive("10.0.0.1")]))

    first = await scanner.scan(profile())
    second = await scanner.scan(profile())

    # One id per pass, carried on every device's event, so a consumer can tell two interleaved
    # scans apart.
    assert first.scan_id != second.scan_id


async def test_scan_an_agent_that_fails_mid_identify_does_not_lose_the_device() -> None:
    class ExplodingTransport(FakeTransport):
        async def get(self, target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]:
            raise RuntimeError("pysnmp returned something nobody expected")

    scanner = build_scanner(
        RecordingSweep([alive("10.0.0.2", ports=(22,))]),
        transport=ExplodingTransport(values=dict(SYS_INFO)),
        communities=["healthy"],
    )

    outcome = await scanner.scan(profile())

    # The ping and the open port are still facts about the address, and they are what WP-4.2
    # matches on when there is no identity.
    assert len(outcome.devices) == 1
    assert outcome.devices[0].identity is None
    assert outcome.devices[0].open_ports == (22,)


async def test_scan_reports_progress_counting_completed_probes_and_the_last_to_answer() -> None:
    sweep = RecordingSweep([
        SweepResult(address="10.0.0.2", responded_to_ping=True),
    ])
    scanner = build_scanner(sweep)
    seen: list[tuple[int, int, str | None]] = []

    await scanner.scan(
        profile(ranges=("10.0.0.0/30",)),
        on_progress=lambda probed, total, last: seen.append((probed, total, last)),
    )

    # The final call always lands, whatever the throttle did with the ones before it: a progress
    # line frozen short of the total reads as a scan that stalled.
    assert seen[-1][0] == seen[-1][1]
    assert seen[-1][2] == "10.0.0.2"


async def test_scan_with_no_progress_observer_still_scans() -> None:
    sweep = RecordingSweep([SweepResult(address="10.0.0.2", responded_to_ping=True)])

    outcome = await build_scanner(sweep).scan(profile(ranges=("10.0.0.0/30",)))

    assert len(outcome.devices) == 1
