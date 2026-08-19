"""The cheap half of a scan: which addresses are there, and which ports they answer on."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import socket
from collections.abc import Awaitable, Callable, Sequence
from dataclasses import dataclass, field
from typing import Protocol

logger = logging.getLogger("discovery.sweep")

#: Packets per address. One, not the poller's three: a sweep asks "is anything there", and three
#: packets against every address in a /24 is three times the traffic to answer the same question. A
#: host that drops the single packet is still found by its open ports.
SWEEP_PACKET_COUNT = 1


class PingHost(Protocol):
    """The part of `icmplib.Host` a sweep reads."""

    @property
    def is_alive(self) -> bool: ...

    @property
    def avg_rtt(self) -> float: ...


PingFunction = Callable[..., Awaitable[PingHost]]

#: Resolves an address to a hostname, or None. Injected so the sweep is testable without a
#: resolver.
ResolveFunction = Callable[[str], Awaitable[str | None]]


@dataclass(frozen=True, slots=True)
class SweepResult:
    """One address that answered something, and what it answered."""

    address: str
    responded_to_ping: bool
    latency_ms: float | None = None
    open_ports: tuple[int, ...] = field(default_factory=tuple)
    hostname: str | None = None
    #: Which protocol produced `hostname`. The sweep itself only ever sets this to `dns`; the
    #: scanner fills in the others for the addresses reverse DNS could not name.
    hostname_source: str | None = None

    @property
    def is_present(self) -> bool:
        """
        Whether anything is there at all.

        An open port counts. A host that filters ICMP but answers on 443 is a host, and treating
        silence as absence is how half an estate goes missing from a CMDB.
        """
        return self.responded_to_ping or bool(self.open_ports)


#: Called once per address as its ping completes: the address, and whether it answered. Sync on
#: purpose — an observer runs inside the sweep's own concurrency and must not await anything.
ProbeObserver = Callable[[str, bool], None]


class NetworkSweep:
    """
    Pings a list of addresses, fingerprints the ports of whatever answered, and resolves its name.

    Concurrency is the whole design. A /24 at one probe at a time with a two-second timeout is
    eight minutes of waiting for nothing; bounded at a few hundred it is a handful of seconds, and
    the bound exists so that a scan does not exhaust the file descriptors of the container it runs
    in.

    Every probe is inside its own `except`. The rule that one dead device never blocks a polling
    cycle applies twice over here, because in a sweep almost every address *is* dead — that is the
    normal case, not the exception.
    """

    def __init__(
        self,
        ping: PingFunction | None = None,
        resolve: ResolveFunction | None = None,
        privileged: bool = True,
        max_concurrency: int = 256,
    ) -> None:
        self._ping = ping
        self._resolve = resolve
        self._privileged = privileged
        self._max_concurrency = max(max_concurrency, 1)

    async def run(
        self,
        addresses: Sequence[str],
        ports: Sequence[int],
        timeout_seconds: float,
        on_probe: ProbeObserver | None = None,
    ) -> list[SweepResult]:
        """
        Sweeps in two passes, and the order is deliberate.

        Everything is pinged first; only the addresses that answered — plus, when the profile names
        ports, every address, because a filtered host answers no ping — reach the fingerprint. Port
        probing every address in a range against six ports would be six times the sweep for the
        sake of the few hosts that hide from ICMP, so it is done only when there are ports to try.

        `on_probe` is called once per address as its ping completes, with the address and whether it
        answered. It is how a caller can say how far a sweep has got — there is deliberately no
        "address being scanned now" to report, because `_max_concurrency` of them are in flight at
        any moment. An observer that raises is not allowed to fail the sweep.
        """
        if not addresses:
            return []

        semaphore = asyncio.Semaphore(self._max_concurrency)
        alive = await asyncio.gather(
            *[self._ping_one(address, timeout_seconds, semaphore, on_probe)
              for address in addresses])
        latencies = dict(zip(addresses, alive, strict=True))

        # With no ports to try, only the addresses that answered a ping exist as far as this scan
        # is concerned. With ports, every address gets the fingerprint — that is the only way a
        # ping-filtered host is ever found, and it is what the operator asked for by naming ports.
        candidates = list(addresses) if ports else [
            address for address, latency in latencies.items() if latency is not None]

        open_ports = dict(zip(
            candidates,
            await asyncio.gather(*[
                self._fingerprint(address, ports, timeout_seconds, semaphore)
                for address in candidates
            ]),
            strict=True,
        )) if ports else {}

        found = [
            SweepResult(
                address=address,
                responded_to_ping=latencies.get(address) is not None,
                latency_ms=latencies.get(address),
                open_ports=open_ports.get(address, ()),
            )
            for address in addresses
        ]
        present = [result for result in found if result.is_present]

        # Resolved only for what was found. Reverse DNS on every address in a range is a second
        # sweep against the resolver, and a range is mostly empty.
        hostnames = await asyncio.gather(
            *[self._resolve_one(result.address, semaphore) for result in present])
        return [
            SweepResult(
                address=result.address,
                responded_to_ping=result.responded_to_ping,
                latency_ms=result.latency_ms,
                open_ports=result.open_ports,
                hostname=hostname,
            )
            for result, hostname in zip(present, hostnames, strict=True)
        ]

    @staticmethod
    def _observe(on_probe: ProbeObserver | None, address: str, answered: bool) -> None:
        if on_probe is None:
            return
        try:
            on_probe(address, answered)
        except Exception:
            # A sweep must not fail because somebody's progress reporting did. One dead target never
            # aborts a cycle (ARCHITECTURE §7.6); neither does one dead observer.
            logger.exception("A sweep progress observer raised; the sweep carried on.")

    async def _ping_one(
        self,
        address: str,
        timeout_seconds: float,
        semaphore: asyncio.Semaphore,
        on_probe: ProbeObserver | None = None,
    ) -> float | None:
        ping = self._ping if self._ping is not None else _default_ping()
        async with semaphore:
            try:
                host = await ping(
                    address,
                    count=SWEEP_PACKET_COUNT,
                    timeout=timeout_seconds,
                    privileged=self._privileged,
                )
            except Exception:
                # Not logged per address. A sweep of a /24 finds 250 silent addresses, and a log
                # line each would bury the ones that answered.
                self._observe(on_probe, address, False)
                return None

        latency = float(host.avg_rtt) if host.is_alive else None
        # Observed after the probe completes, never before it starts: this counts what has been
        # done, and a count of what has been *begun* would run ahead of the truth by the whole
        # concurrency window.
        self._observe(on_probe, address, latency is not None)
        return latency

    async def _fingerprint(
        self,
        address: str,
        ports: Sequence[int],
        timeout_seconds: float,
        semaphore: asyncio.Semaphore,
    ) -> tuple[int, ...]:
        results = await asyncio.gather(
            *[self._connect(address, port, timeout_seconds, semaphore) for port in ports])
        return tuple(port for port, is_open in zip(ports, results, strict=True) if is_open)

    async def _connect(
        self,
        address: str,
        port: int,
        timeout_seconds: float,
        semaphore: asyncio.Semaphore,
    ) -> bool:
        async with semaphore:
            writer = None
            try:
                _, writer = await asyncio.wait_for(
                    asyncio.open_connection(address, port), timeout=timeout_seconds)
                return True
            except (TimeoutError, OSError):
                # A refusal and a timeout are both "not open" here. The distinction matters to a
                # service check, which is monitoring a port somebody said would be there; a scan is
                # asking whether anything is listening.
                return False
            finally:
                if writer is not None:
                    writer.close()
                    with contextlib.suppress(Exception):
                        await writer.wait_closed()

    async def _resolve_one(self, address: str, semaphore: asyncio.Semaphore) -> str | None:
        resolve = self._resolve if self._resolve is not None else _default_resolve
        async with semaphore:
            try:
                return await resolve(address)
            except Exception:
                return None


def _default_ping() -> PingFunction:
    # Imported lazily so this module — and every test in it — imports on a machine without the
    # capability icmplib needs to open its socket.
    from icmplib import async_ping

    return async_ping  # type: ignore[no-any-return]


async def _default_resolve(address: str) -> str | None:
    """
    Reverse DNS, with the resolver's failure treated as "no name".

    `getnameinfo` rather than `gethostbyaddr` because it is the one the event loop exposes without
    a thread, and NI_NAMEREQD is what makes it answer None instead of handing back the address as
    its own name — which would put a hostname of "10.0.0.7" on every discovery.
    """
    loop = asyncio.get_running_loop()
    try:
        hostname, _ = await loop.getnameinfo((address, 0), socket.NI_NAMEREQD)
    except (OSError, socket.gaierror):
        return None
    return hostname if hostname and hostname != address else None
