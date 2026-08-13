from __future__ import annotations

import asyncio
from collections.abc import Sequence
from dataclasses import dataclass

from discovery.sweep import NetworkSweep


@dataclass(frozen=True, slots=True)
class FakeHost:
    is_alive: bool
    avg_rtt: float = 0.0


class FakePing:
    """
    Answers for a fixed set of live addresses, and records the concurrency it was driven at.

    The peak matters: a sweep's whole design is that a /24 finishes in seconds, which only happens
    if the probes overlap. A regression to one-at-a-time would still pass every result assertion.
    """

    def __init__(self, alive: Sequence[str], failing: Sequence[str] = ()) -> None:
        self._alive = set(alive)
        self._failing = set(failing)
        self.calls: list[str] = []
        self.in_flight = 0
        self.peak_in_flight = 0

    async def __call__(self, address: str, **_: object) -> FakeHost:
        self.calls.append(address)
        self.in_flight += 1
        self.peak_in_flight = max(self.peak_in_flight, self.in_flight)
        try:
            await asyncio.sleep(0)
            if address in self._failing:
                raise OSError("Root privileges are required.")
            return FakeHost(is_alive=address in self._alive, avg_rtt=1.25)
        finally:
            self.in_flight -= 1


async def resolve_none(_: str) -> str | None:
    return None


async def test_run_reports_only_the_addresses_that_answered() -> None:
    ping = FakePing(alive=["10.0.0.2"])
    sweep = NetworkSweep(ping=ping, resolve=resolve_none)

    found = await sweep.run(["10.0.0.1", "10.0.0.2", "10.0.0.3"], ports=[], timeout_seconds=1)

    assert [result.address for result in found] == ["10.0.0.2"]
    assert found[0].responded_to_ping is True
    assert found[0].latency_ms == 1.25
    # Every address is still probed: the sweep is what decides which ones exist.
    assert len(ping.calls) == 3


async def test_run_probes_addresses_concurrently() -> None:
    ping = FakePing(alive=[])
    sweep = NetworkSweep(ping=ping, resolve=resolve_none, max_concurrency=8)

    await sweep.run([f"10.0.0.{octet}" for octet in range(1, 33)], ports=[], timeout_seconds=1)

    assert ping.peak_in_flight > 1
    # And bounded, so a /16 cannot exhaust the container's file descriptors.
    assert ping.peak_in_flight <= 8


async def test_run_a_ping_that_raises_is_one_dead_address_not_a_failed_sweep() -> None:
    ping = FakePing(alive=["10.0.0.3"], failing=["10.0.0.1"])
    sweep = NetworkSweep(ping=ping, resolve=resolve_none)

    found = await sweep.run(["10.0.0.1", "10.0.0.2", "10.0.0.3"], ports=[], timeout_seconds=1)

    assert [result.address for result in found] == ["10.0.0.3"]


async def test_run_with_no_addresses_does_nothing_rather_than_failing() -> None:
    ping = FakePing(alive=[])

    found = await NetworkSweep(ping=ping, resolve=resolve_none).run([], ports=[80],
    timeout_seconds=1)

    assert found == []
    assert ping.calls == []


async def test_run_finds_a_host_that_filters_icmp_but_answers_a_port() -> None:
    # The reason a fingerprint runs against every address rather than only the ones that pinged:
    # treating silence as absence is how half an estate goes missing from a CMDB.
    ping = FakePing(alive=[])
    listener = await asyncio.start_server(lambda *_: None, "127.0.0.1", 0)
    port = int(listener.sockets[0].getsockname()[1])

    try:
        found = await NetworkSweep(ping=ping, resolve=resolve_none).run(
            ["127.0.0.1"], ports=[port], timeout_seconds=2)
    finally:
        listener.close()
        await listener.wait_closed()

    assert len(found) == 1
    assert found[0].responded_to_ping is False
    assert found[0].open_ports == (port,)
    assert found[0].is_present is True


async def test_run_a_closed_port_is_not_reported_as_open() -> None:
    ping = FakePing(alive=["127.0.0.1"])
    # Bound and immediately closed, so the port is almost certainly free and refuses the connect.
    listener = await asyncio.start_server(lambda *_: None, "127.0.0.1", 0)
    port = int(listener.sockets[0].getsockname()[1])
    listener.close()
    await listener.wait_closed()

    found = await NetworkSweep(ping=ping, resolve=resolve_none).run(
        ["127.0.0.1"], ports=[port], timeout_seconds=2)

    assert found[0].open_ports == ()
    # Still present, because it answered the ping.
    assert found[0].is_present is True


async def test_run_resolves_a_hostname_only_for_what_it_found() -> None:
    ping = FakePing(alive=["10.0.0.2"])
    resolved: list[str] = []

    async def resolve(address: str) -> str | None:
        resolved.append(address)
        return "switch.example.test"

    found = await NetworkSweep(ping=ping, resolve=resolve).run(
        ["10.0.0.1", "10.0.0.2"], ports=[], timeout_seconds=1)

    assert found[0].hostname == "switch.example.test"
    # Reverse DNS on every address in a mostly-empty range is a second sweep against the resolver.
    assert resolved == ["10.0.0.2"]


async def test_run_a_resolver_failure_leaves_the_device_nameless_rather_than_lost() -> None:
    ping = FakePing(alive=["10.0.0.2"])

    async def resolve(_: str) -> str | None:
        raise OSError("Temporary failure in name resolution")

    found = await NetworkSweep(ping=ping, resolve=resolve).run(
        ["10.0.0.2"], ports=[], timeout_seconds=1)

    assert len(found) == 1
    assert found[0].hostname is None
