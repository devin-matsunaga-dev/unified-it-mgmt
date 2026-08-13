"""One scan of one profile: sweep the range, then interrogate whatever answered."""

from __future__ import annotations

import asyncio
import logging
import uuid
from collections.abc import Sequence
from dataclasses import dataclass, field

from .config import ScanProfile
from .identify import Neighbour, SnmpIdentity, identify, walk_neighbours
from .ranges import LocalResolver, RangeError, expand_all
from .snmp import SnmpTransport, default_transport
from .sweep import NetworkSweep, SweepResult

logger = logging.getLogger("discovery.scanner")

#: How many devices are identified at once. Far below the sweep's concurrency on purpose: an
#: identify is up to five SNMP round trips against a device that answered, so a hundred at once is
#: a burst of traffic at the exact devices the scan cares most about not upsetting.
IDENTIFY_CONCURRENCY = 16


@dataclass(frozen=True, slots=True)
class DiscoveredDevice:
    """Everything one scan learned about one address."""

    address: str
    responded_to_ping: bool
    open_ports: tuple[int, ...] = field(default_factory=tuple)
    hostname: str | None = None
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
    ) -> None:
        self._sweep = sweep
        self._transport = transport
        self._communities = tuple(communities)
        self._local = local
        self._identify_concurrency = max(identify_concurrency, 1)

    async def scan(self, profile: ScanProfile) -> ScanOutcome:
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

        found = await self._sweep.run(addresses, profile.ports, profile.timeout_seconds)
        devices = await self._identify_all(profile, found)

        return ScanOutcome(
            profile_id=profile.profile_id,
            profile_name=profile.name,
            scan_id=scan_id,
            addresses_probed=len(addresses),
            devices=tuple(devices),
            range_errors=range_errors,
        )

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
            identity=identity,
            neighbours=neighbours,
        )

    def _community_index(self, community: str) -> int:
        """Which configured community this was, 1-based. Zero for one not in the list at all."""
        return self._communities.index(community) + 1 if community in self._communities else 0
