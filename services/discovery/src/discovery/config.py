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


@dataclass(frozen=True, slots=True)
class ConfigApplied:
    """What one config response changed, for the log line that follows it."""

    profile_count: int


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

    def apply(self, response: Mapping[str, Any]) -> ConfigApplied:
        parsed = parse_profiles(response)
        self.profiles = {profile.profile_id: profile for profile in parsed}
        return ConfigApplied(profile_count=len(self.profiles))

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
        profile_id = str(item.get("scanProfileId") or "")
        ranges = tuple(
            str(entry).strip() for entry in (item.get("ranges") or []) if str(entry).strip())
        if not profile_id or not ranges:
            continue

        profiles.append(ScanProfile(
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
        ))
    return profiles


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
