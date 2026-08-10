from __future__ import annotations

from typing import Any

from poller.config import ConfigState


def snapshot(*device_ids: str, version: int = 1) -> dict[str, Any]:
    return {
        "configVersion": version,
        "isFullSnapshot": True,
        "devices": [{"deviceId": device_id, "address": f"10.0.0.{index}", "checks": []}
                    for index, device_id in enumerate(device_ids, start=1)],
        "removedDeviceIds": [],
        "maintenanceWindows": [],
    }


def delta(
    *,
    version: int,
    upserted: list[str] | None = None,
    removed: list[str] | None = None,
    windows: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    return {
        "configVersion": version,
        "isFullSnapshot": False,
        "devices": [{"deviceId": device_id, "address": "10.0.0.9", "checks": []}
                    for device_id in (upserted or [])],
        "removedDeviceIds": removed or [],
        "maintenanceWindows": windows or [],
    }


def test_apply_full_snapshot_replaces_everything_held() -> None:
    state = ConfigState()
    state.apply(snapshot("a", "b", version=4))

    applied = state.apply(snapshot("c", version=9))

    assert set(state.devices) == {"c"}
    assert state.version == 9
    assert applied.full_snapshot is True
    assert applied.device_count == 1


def test_apply_delta_upserts_and_removes_without_touching_the_rest() -> None:
    state = ConfigState()
    state.apply(snapshot("a", "b", version=4))

    applied = state.apply(delta(version=7, upserted=["b", "c"], removed=["a"]))

    assert set(state.devices) == {"b", "c"}
    assert state.version == 7
    assert applied.upserted == 2
    assert applied.removed == 1


def test_apply_delta_replaces_a_device_whole_rather_than_merging_it() -> None:
    state = ConfigState()
    state.apply(snapshot("a", version=1))
    state.devices["a"]["checks"] = [{"name": "CPU"}]

    state.apply(delta(version=2, upserted=["a"]))

    # The server re-sends a device whole when any of its checks change, so a merge here would keep
    # a check the operator has just deleted.
    assert state.devices["a"]["checks"] == []


def test_apply_maintenance_windows_are_replaced_not_accumulated() -> None:
    state = ConfigState()
    state.apply(delta(version=1, windows=[{"id": "w1"}, {"id": "w2"}]))

    state.apply(delta(version=2, windows=[{"id": "w2"}]))

    assert [window["id"] for window in state.maintenance_windows] == ["w2"]


def test_forget_drops_the_version_so_the_next_fetch_is_a_snapshot() -> None:
    state = ConfigState()
    state.apply(snapshot("a", version=12))

    state.forget()

    assert state.version == 0
    assert state.devices == {}
