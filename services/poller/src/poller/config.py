"""What the poller believes it should be doing, and how a config response changes that."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(frozen=True, slots=True)
class ConfigApplied:
    """What one config response changed, for the log line that follows it."""

    version: int
    full_snapshot: bool
    upserted: int
    removed: int
    device_count: int


@dataclass(slots=True)
class ConfigState:
    """
    The devices this poller is responsible for, keyed by device id.

    A device arrives whole or not at all — a check edit re-sends its device — so applying an update
    is a replacement rather than a merge. That is the server's rule (WP-3.1) and copying it here is
    the whole reason a poller can hold a delta safely.
    """

    version: int = 0
    devices: dict[str, dict[str, Any]] = field(default_factory=dict)
    maintenance_windows: list[dict[str, Any]] = field(default_factory=list)

    def apply(self, response: dict[str, Any]) -> ConfigApplied:
        full_snapshot = bool(response.get("isFullSnapshot"))
        devices = response.get("devices") or []
        removed = response.get("removedDeviceIds") or []

        if full_snapshot:
            # A snapshot is the complete answer for this group, so anything not in it is gone.
            self.devices = {str(device["deviceId"]): device for device in devices}
        else:
            for device in devices:
                self.devices[str(device["deviceId"])] = device
            for device_id in removed:
                self.devices.pop(str(device_id), None)

        # Windows are always sent whole; muting the wrong device is worse than re-reading a short
        # list, so there is nothing to merge.
        self.maintenance_windows = list(response.get("maintenanceWindows") or [])
        self.version = int(response.get("configVersion") or 0)

        return ConfigApplied(
            version=self.version,
            full_snapshot=full_snapshot,
            upserted=len(devices),
            removed=0 if full_snapshot else len(removed),
            device_count=len(self.devices),
        )

    def forget(self) -> None:
        """
        Drops everything and asks for a full snapshot next time.

        Reached when the server refuses the version this poller holds — at that point its idea of
        history is wrong, and the only honest recovery is to start again from nothing.
        """
        self.version = 0
        self.devices.clear()
        self.maintenance_windows.clear()
