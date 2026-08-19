"""One scan of one profile: sweep the range, then interrogate whatever answered."""

from __future__ import annotations

import asyncio
import logging
import time
import uuid
from collections.abc import Awaitable, Callable, Sequence
from dataclasses import dataclass, field

from .config import ScanProfile
from .identify import Neighbour, SnmpIdentity, identify, walk_neighbours
from .names import SOURCE_DNS, ResolvedName, resolve_name
from .ranges import LocalResolver, RangeError, expand_all
from .snmp import SnmpTransport, default_transport
from .sweep import NetworkSweep, SweepResult

logger = logging.getLogger("discovery.scanner")

#: How many devices are identified at once. Far below the sweep's concurrency on purpose: an
#: identify is up to five SNMP round trips against a device that answered, so a hundred at once is
#: a burst of traffic at the exact devices the scan cares most about not upsetting.
IDENTIFY_CONCURRENCY = 16

#: How often a sweep in flight says how far it has got. A second is fast enough to look live and
#: slow enough that a /24 costs a handful of requests rather than 254.
PROGRESS_INTERVAL_SECONDS = 1.0


@dataclass(frozen=True, slots=True)
class DiscoveredDevice:
    """Everything one scan learned about one address."""

    address: str
    responded_to_ping: bool
    open_ports: tuple[int, ...] = field(default_factory=tuple)
    hostname: str | None = None
    #: Which protocol produced `hostname`: `dns`, `mdns` or `netbios`. None when nothing named it.
    #: Carried so an approver can weigh the name — a PTR record and a NetBIOS answer are not equally
    #: trustworthy, and the review card says which one it is showing.
    hostname_source: str | None = None
    identity: SnmpIdentity | None = None
    neighbours: tuple[Neighbour, ...] = field(default_factory=tuple)


@dataclass(frozen=True, slots=True)
class ScanOutcome:
    """
    What one pass of one profile produced.

    `scan_id` is generated per pass and travels on every device's event, so a consumer can tell
    "the estate as this scan saw it" from two scans interleaved. `addresses_probed` is what makes
    an empty result legible: zero devices out of eight addresses is a clean scan of an empty range,
    and zero out of zero is a profile whose ranges did not expand.
    """

    profile_id: str
    profile_name: str
    scan_id: str
    addresses_probed: int
    devices: tuple[DiscoveredDevice, ...] = field(default_factory=tuple)
    range_errors: tuple[str, ...] = field(default_factory=tuple)


def _with_name(result: SweepResult, resolved: ResolvedName | None) -> SweepResult:
    """
    Attaches a name and its provenance, preferring what reverse DNS already found.

    A PTR record is the strongest of the three: it is what the network's own administrator
    published, while mDNS and NetBIOS names are whatever the device says about itself.
    """
    if result.hostname is not None:
        return SweepResult(
            address=result.address,
            responded_to_ping=result.responded_to_ping,
            latency_ms=result.latency_ms,
            open_ports=result.open_ports,
            hostname=result.hostname,
            hostname_source=SOURCE_DNS,
        )

    return SweepResult(
        address=result.address,
        responded_to_ping=result.responded_to_ping,
        latency_ms=result.latency_ms,
        open_ports=result.open_ports,
        hostname=resolved.name if resolved else None,
        hostname_source=resolved.source if resolved else None,
    )


#: Names an address when reverse DNS could not. Injectable so tests need no network.
NameResolver = Callable[[str], Awaitable[ResolvedName | None]]


#: Reports how far a sweep has got: addresses probed, the total, and the last one that answered.
#: There is deliberately no "current address" — the sweep runs hundreds of probes at once.
ScanProgress = Callable[[int, int, str | None], None]


class _ProgressCounter:
    """
    Turns per-probe callbacks into a running total, and calls out at most once a second.

    Throttled here rather than at the caller because the callback fires once per address: a /24 is
    254 calls in a few seconds, and a scanner that posted each one would spend the sweep talking
    about the sweep.
    """

    __slots__ = ("_last_address", "_probed", "_report", "_reported_at", "_total")

    def __init__(self, total: int, report: ScanProgress) -> None:
        self._total = total
        self._report = report
        self._probed = 0
        self._last_address: str | None = None
        self._reported_at = 0.0

    def probed(self, address: str, answered: bool) -> None:
        self._probed += 1
        if answered:
            self._last_address = address

        now = time.monotonic()
        # The last address is always reported, however late it lands: finishing silently after the
        # final tick would leave a progress line frozen short of the total.
        if now - self._reported_at < PROGRESS_INTERVAL_SECONDS and self._probed < self._total:
            return

        self._reported_at = now
        self._report(self._probed, self._total, self._last_address)


class Scanner:
    """
    Runs a profile end to end.

    Nothing in here raises for one address, one community or one range: a scan is a walk through a
    space that is mostly empty and partly hostile, so every step that can fail is contained at the
    smallest unit that still leaves a useful answer. A profile whose every range is malformed
    produces an outcome with no devices and the reasons attached, which is the difference between
    "nothing is there" and "nothing was looked at".
    """

    def __init__(
        self,
        sweep: NetworkSweep,
        transport: SnmpTransport | None = None,
        communities: Sequence[str] = (),
        local: LocalResolver | None = None,
        identify_concurrency: int = IDENTIFY_CONCURRENCY,
        name_resolver: NameResolver | None = None,
    ) -> None:
        self._sweep = sweep
        self._transport = transport
        self._communities = tuple(communities)
        self._local = local
        self._identify_concurrency = max(identify_concurrency, 1)
        # Injectable for the reason the ping and the SNMP transport are: a unit test that reached
        # the network would wait out a real timeout per address and answer differently on every
        # machine it ran on.
        self._name_resolver = name_resolver or resolve_name

    async def scan(
        self,
        profile: ScanProfile,
        on_progress: ScanProgress | None = None,
    ) -> ScanOutcome:
        scan_id = str(uuid.uuid4())
        addresses, range_errors = self._expand(profile)

        if not addresses:
            return ScanOutcome(
                profile_id=profile.profile_id,
                profile_name=profile.name,
                scan_id=scan_id,
                addresses_probed=0,
                range_errors=range_errors,
            )

        # The total is only knowable here: a profile scanning `local` has no size until the ranges
        # are expanded against the machine the scanner is actually on.
        observer = _ProgressCounter(len(addresses), on_progress) if on_progress else None
        found = await self._sweep.run(
            addresses, profile.ports, profile.timeout_seconds,
            on_probe=observer.probed if observer else None)
        named = await self._name_all(found)
        devices = await self._identify_all(profile, named)

        return ScanOutcome(
            profile_id=profile.profile_id,
            profile_name=profile.name,
            scan_id=scan_id,
            addresses_probed=len(addresses),
            devices=tuple(devices),
            range_errors=range_errors,
        )

    async def _name_all(self, found: Sequence[SweepResult]) -> list[SweepResult]:
        """
        Gives a name to whatever reverse DNS could not.

        Only the addresses that answered and have no name are asked, which on a real network is most
        of them and on a well-run one is none: a LAN with proper PTR records reaches this and does
        nothing. Bounded to the same concurrency an identify uses, because each unnamed address is
        two datagrams and a timeout, and a /24 of unnamed hosts would otherwise be 500 in flight.
        """
        unnamed = [result for result in found if result.hostname is None]
        if not unnamed:
            return list(found)

        semaphore = asyncio.Semaphore(self._identify_concurrency)

        async def named(result: SweepResult) -> tuple[str, ResolvedName | None]:
            async with semaphore:
                try:
                    return result.address, await self._name_resolver(result.address)
                except Exception:
                    # One address that could not be named never costs the scan the other 253.
                    return result.address, None

        resolved = dict(await asyncio.gather(*[named(result) for result in unnamed]))
        for address, name in resolved.items():
            if name is not None:
                logger.info(
                    "Named an address that reverse DNS could not.",
                    extra={"address": address, "hostname": name.name, "source": name.source},
                )

        return [_with_name(result, resolved.get(result.address)) for result in found]

    def _expand(self, profile: ScanProfile) -> tuple[list[str], tuple[str, ...]]:
        """
        Expands the profile's ranges, keeping the ones that worked.

        One malformed range does not lose the other four. The API validated all of them on the way
        in, so an error here is a row written behind it, a range that only fails where the scanner
        runs (`local` on a host with no usable route), or the two implementations having drifted —
        and all three are worth reporting rather than crashing on.
        """
        errors: list[str] = []
        seen: set[str] = set()
        addresses: list[str] = []
        for text in profile.ranges:
            try:
                expanded = expand_all([text], self._local)
            except RangeError as error:
                errors.append(f"{text}: {error}")
                continue

            # Deduplicated across ranges as well as within one, because two overlapping blocks must
            # not probe the same address twice or publish it as two devices.
            for address in expanded:
                if address not in seen:
                    seen.add(address)
                    addresses.append(address)

        return addresses, tuple(errors)

    async def _identify_all(
        self,
        profile: ScanProfile,
        found: Sequence[SweepResult],
    ) -> list[DiscoveredDevice]:
        if not profile.snmp_enabled or not self._communities or not found:
            return [
                DiscoveredDevice(
                    address=result.address,
                    responded_to_ping=result.responded_to_ping,
                    open_ports=result.open_ports,
                    hostname=result.hostname,
                    hostname_source=result.hostname_source,
                )
                for result in found
            ]

        semaphore = asyncio.Semaphore(self._identify_concurrency)
        return list(await asyncio.gather(
            *[self._identify_one(profile, result, semaphore) for result in found]))

    async def _identify_one(
        self,
        profile: ScanProfile,
        result: SweepResult,
        semaphore: asyncio.Semaphore,
    ) -> DiscoveredDevice:
        transport = self._transport if self._transport is not None else default_transport()
        async with semaphore:
            identity = await identify(
                result.address,
                self._communities,
                transport,
                timeout_seconds=profile.timeout_seconds,
            )

            # Neighbours are walked with the community that already worked, never by trying the
            # list again: the identify has just established which one this device answers on, and a
            # second sweep through the communities would be three more failed authentications per
            # device.
            neighbours: tuple[Neighbour, ...] = ()
            if identity is not None and profile.neighbour_discovery_enabled:
                neighbours = await walk_neighbours(
                    result.address,
                    identity.community,
                    transport,
                    timeout_seconds=profile.timeout_seconds,
                )

        if identity is not None:
            # The community's *position* in the configured list, never its value. It says which
            # credential a device answered on — the thing somebody needs in order to enrol it —
            # while keeping the secret out of a log file, which is the rule WP-3.11 set for the
            # poller.
            logger.info(
                "Device identified.",
                extra={
                    "address": result.address,
                    "sys_name": identity.sys_name,
                    "community_index": self._community_index(identity.community),
                    "neighbours": len(neighbours),
                },
            )

        return DiscoveredDevice(
            address=result.address,
            responded_to_ping=result.responded_to_ping,
            open_ports=result.open_ports,
            hostname=result.hostname,
            hostname_source=result.hostname_source,
            identity=identity,
            neighbours=neighbours,
        )

    def _community_index(self, community: str) -> int:
        """Which configured community this was, 1-based. Zero for one not in the list at all."""
        return self._communities.index(community) + 1 if community in self._communities else 0
