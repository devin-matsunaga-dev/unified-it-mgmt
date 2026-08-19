from __future__ import annotations

from typing import Any

from discovery.config import (
    DEFAULT_INTERVAL_SECONDS,
    DEFAULT_TIMEOUT_SECONDS,
    ConfigState,
    parse_profiles,
)


def response(*profiles: dict[str, Any]) -> dict[str, Any]:
    return {
        "discoveryGroup": "default",
        "generatedAt": "2026-08-13T00:00:00+00:00",
        "profiles": list(profiles),
    }


def profile(
    profile_id: str = "0199c0de-4100-7000-8000-000000000001",
    **overrides: Any,
) -> dict[str, Any]:
    return {
        "scanProfileId": profile_id,
        "name": "Local subnet sweep",
        "ranges": ["local"],
        "ports": [22, 443],
        "intervalSeconds": 300,
        "timeoutSeconds": 2,
        "snmpEnabled": True,
        "neighbourDiscoveryEnabled": True,
    } | overrides


def test_parse_profiles_reads_every_field() -> None:
    parsed = parse_profiles(response(profile()))

    assert len(parsed) == 1
    only = parsed[0]
    assert only.name == "Local subnet sweep"
    assert only.ranges == ("local",)
    assert only.ports == (22, 443)
    assert only.interval_seconds == 300
    assert only.timeout_seconds == 2
    assert only.snmp_enabled is True
    assert only.neighbour_discovery_enabled is True


def test_parse_profiles_skips_a_profile_with_no_id_or_no_range() -> None:
    # One malformed entry must not cost the other nine. The API validates these, so anything
    # reaching here arrived some other way.
    parsed = parse_profiles(response(
        profile(profile_id=""),
        profile(profile_id="b", ranges=[]),
        profile(profile_id="c"),
    ))

    assert [item.profile_id for item in parsed] == ["c"]


def test_parse_profiles_drops_a_port_outside_the_legal_range() -> None:
    parsed = parse_profiles(response(profile(ports=[22, 0, 70_000, "http", 443])))

    assert parsed[0].ports == (22, 443)


def test_parse_profiles_defaults_an_absent_interval_and_timeout() -> None:
    parsed = parse_profiles(response(profile(intervalSeconds=None, timeoutSeconds=0)))

    assert parsed[0].interval_seconds == DEFAULT_INTERVAL_SECONDS
    assert parsed[0].timeout_seconds == DEFAULT_TIMEOUT_SECONDS


def test_parse_profiles_treats_absent_flags_as_on() -> None:
    # A profile written by an older client is one the useful scan is the thorough one for.
    document = profile()
    del document["snmpEnabled"]
    del document["neighbourDiscoveryEnabled"]

    parsed = parse_profiles(response(document))

    assert parsed[0].snmp_enabled is True
    assert parsed[0].neighbour_discovery_enabled is True


def test_parse_profiles_trims_range_text_and_drops_blank_entries() -> None:
    parsed = parse_profiles(response(profile(ranges=[" 10.0.0.0/24 ", "  ", "local"])))

    assert parsed[0].ranges == ("10.0.0.0/24", "local")


def test_apply_replaces_the_whole_list_rather_than_merging_it() -> None:
    state = ConfigState()
    state.apply(response(profile("a"), profile("b")))

    applied = state.apply(response(profile("c")))

    # The server sends the list whole, so a profile absent from a response has been disabled,
    # deleted or moved to another group — and all three mean "stop scanning it".
    assert set(state.profiles) == {"c"}
    assert applied.profile_count == 1


def test_apply_an_empty_response_leaves_nothing_scheduled() -> None:
    state = ConfigState()
    state.apply(response(profile("a")))

    applied = state.apply(response())

    assert state.profiles == {}
    assert applied.profile_count == 0


# --- the schedule switches (Phase 5.5) ---


def test_a_profile_without_the_schedule_flag_is_scheduled() -> None:
    parsed = parse_profiles(response(profile()))

    # Absent means on, like every other flag here. A response from a platform older than the switch
    # must not read as "this estate asked to stop scanning".
    assert parsed[0].schedule_enabled is True


def test_a_response_without_the_estate_switch_leaves_scheduled_scanning_on() -> None:
    state = ConfigState()

    applied = state.apply(response(profile()))

    assert state.scheduled_scanning_enabled is True
    assert applied.scheduled_scanning_enabled is True


def test_the_estate_switch_off_empties_the_scheduled_list_and_keeps_the_profiles() -> None:
    state = ConfigState()
    document = response(profile("a"), profile("b"))
    document["scheduledScanningEnabled"] = False

    state.apply(document)

    # Held, not forgotten: an on-demand run names a profile the scanner must still be holding.
    assert set(state.profiles) == {"a", "b"}
    assert state.scheduled() == []


def test_a_profile_with_its_own_schedule_off_is_held_but_not_scheduled() -> None:
    state = ConfigState()

    state.apply(response(profile("a"), profile("b", scheduleEnabled=False)))

    assert set(state.profiles) == {"a", "b"}
    assert [item.profile_id for item in state.scheduled()] == ["a"]
