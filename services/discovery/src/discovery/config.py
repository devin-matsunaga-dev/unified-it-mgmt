"""What the scanner believes it should be scanning, and how a config response changes that."""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Any

#: Used when a profile arrives without one. The server validates intervals, so this only covers a
#: profile that reached the scanner through some other route.
DEFAULT_INTERVAL_SECONDS = 3_600

#: Used when a profile carries no timeout. Deliberately small: this is one probe against one
#: address, and most addresses in a range do not answer at all.
DEFAULT_TIMEOUT_SECONDS = 2


@dataclass(frozen=True, slots=True)
class ScanProfile:
    """One range list to sweep, and how thoroughly to interrogate what answers."""

    profile_id: str
    name: str
    ranges: tuple[str, ...]
    ports: tuple[int, ...]
    interval_seconds: int
    timeout_seconds: float
    snmp_enabled: bool
    neighbour_discovery_enabled: bool

    #: Whether `interval_seconds` means anything for this profile. False makes it on-demand only:
    #: it is still held and can still be named by a requested run, but no cycle starts it.
    schedule_enabled: bool = True


@dataclass(frozen=True, slots=True)
class ConfigApplied:
    """What one config response changed, for the log line that follows it."""

    profile_count: int
    scheduled_scanning_enabled: bool = True


@dataclass(slots=True)
class ConfigState:
    """
    The scan profiles this service is responsible for, keyed by id.

    Replaced wholesale on every fetch rather than merged, because the server sends the list whole —
    there are no deltas to reconcile and no version to hold. A profile that disappears from the
    response has been disabled, deleted or moved to another group, and all three mean "stop
    scanning it" without the scanner having to tell them apart.
    """

    profiles: dict[str, ScanProfile] = field(default_factory=dict)

    #: The estate-wide switch, as the last config response left it. False stops every *scheduled*
    #: sweep in this group and stops nothing else — a run somebody asked for still runs, because
    #: the switch is aimed at the clock rather than at the scanner.
    scheduled_scanning_enabled: bool = True

    def apply(self, response: Mapping[str, Any]) -> ConfigApplied:
        parsed = parse_profiles(response)
        self.profiles = {profile.profile_id: profile for profile in parsed}
        # Absent means on, like every other flag here: a response without the field came from a
        # platform older than the switch, and defaulting a kill switch to "off" on a field nobody
        # sent would stop an estate scanning for a reason nobody could see.
        self.scheduled_scanning_enabled = bool(response.get("scheduledScanningEnabled", True))
        return ConfigApplied(
            profile_count=len(self.profiles),
            scheduled_scanning_enabled=self.scheduled_scanning_enabled,
        )

    def scheduled(self) -> list[ScanProfile]:
        """
        The profiles a cycle may start on its own — everything, unless the clock has been switched
        off somewhere. Requested runs do not come through here; they arrive already chosen.
        """
        if not self.scheduled_scanning_enabled:
            return []
        return [profile for profile in self.profiles.values() if profile.schedule_enabled]

    def forget(self) -> None:
        """Drops every profile. Nothing calls it today; it is here for symmetry with the poller."""
        self.profiles.clear()


def parse_profiles(response: Mapping[str, Any]) -> list[ScanProfile]:
    """
    Reads the config document, skipping anything malformed rather than failing the cycle.

    A profile with no id or no range cannot be scanned, and refusing the whole document because one
    entry is wrong would stop a scanner that has nine good profiles.
    """
    profiles: list[ScanProfile] = []
    for item in response.get("profiles") or []:
        if (profile := parse_profile(item)) is not None:
            profiles.append(profile)
    return profiles


def parse_profile(item: Mapping[str, Any]) -> ScanProfile | None:
    """
    One profile, or None if it cannot be scanned.

    Shared by the config list and by a requested run, which carries its whole profile rather than
    an id — so what a profile *is* is decided once, however the scanner came to hear about it.
    """
    profile_id = str(item.get("scanProfileId") or "")
    ranges = tuple(
        str(entry).strip() for entry in (item.get("ranges") or []) if str(entry).strip())
    if not profile_id or not ranges:
        return None

    return ScanProfile(
        profile_id=profile_id,
        name=str(item.get("name") or ""),
        ranges=ranges,
        ports=tuple(_ports(item.get("ports"))),
        interval_seconds=_positive(item.get("intervalSeconds"), DEFAULT_INTERVAL_SECONDS),
        timeout_seconds=float(_positive(item.get("timeoutSeconds"), DEFAULT_TIMEOUT_SECONDS)),
        # Absent means on, matching the API's own defaults: a profile that arrived without the
        # flags is one written by an older client, and the useful scan is the thorough one.
        snmp_enabled=bool(item.get("snmpEnabled", True)),
        neighbour_discovery_enabled=bool(item.get("neighbourDiscoveryEnabled", True)),
        schedule_enabled=bool(item.get("scheduleEnabled", True)),
    )


def _ports(raw: Any) -> list[int]:
    ports: list[int] = []
    for entry in raw or []:
        try:
            port = int(entry)
        except (TypeError, ValueError):
            continue
        if 1 <= port <= 65535:
            ports.append(port)
    return ports


def _positive(raw: Any, default: int) -> int:
    try:
        value = int(raw)
    except (TypeError, ValueError):
        return default
    return value if value > 0 else default
