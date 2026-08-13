from __future__ import annotations

import pytest

from discovery.settings import DEFAULT_DISCOVERED_EXCHANGE, MissingSettingError, Settings

REQUIRED = {
    "DISCOVERY_NAME": "discovery-1",
    "DISCOVERY_API_BASE_URL": "http://api:5000/",
    "DISCOVERY_OIDC_TOKEN_URL": "http://keycloak/token",
    "DISCOVERY_OIDC_CLIENT_ID": "it-platform-discovery",
    "DISCOVERY_OIDC_CLIENT_SECRET": "shhh",
    "DISCOVERY_AMQP_URL": "amqp://discovery:shhh@rabbitmq:5672/",
}


def test_from_env_reads_every_required_value_and_defaults_the_rest() -> None:
    settings = Settings.from_env(REQUIRED)

    assert settings.name == "discovery-1"
    assert settings.discovery_group == "default"
    assert settings.discovered_exchange == DEFAULT_DISCOVERED_EXCHANGE
    # Trailing slash removed once, here, so no caller has to think about double slashes.
    assert settings.api_base_url == "http://api:5000"
    assert settings.interval_seconds == 30
    assert settings.max_concurrent_probes == 256
    assert settings.icmp_privileged is True
    assert settings.snmp_communities == ("public",)


@pytest.mark.parametrize("missing", sorted(REQUIRED))
def test_from_env_missing_a_required_value_crashes_naming_it(missing: str) -> None:
    environ = {key: value for key, value in REQUIRED.items() if key != missing}

    with pytest.raises(MissingSettingError) as error:
        Settings.from_env(environ)

    assert error.value.name == missing


def test_from_env_communities_are_ordered_and_trimmed() -> None:
    settings = Settings.from_env(
        REQUIRED | {"DISCOVERY_SNMP_COMMUNITIES": " healthy , degraded ,, public "})

    # Order is the order they are tried, so it is part of the setting rather than incidental.
    assert settings.snmp_communities == ("healthy", "degraded", "public")


def test_from_env_an_empty_community_list_falls_back_to_the_default() -> None:
    # A blank value must not leave the scanner with nothing to try, which would silently turn every
    # identify off while the SNMP flag on each profile still said it was on.
    settings = Settings.from_env(REQUIRED | {"DISCOVERY_SNMP_COMMUNITIES": " , "})

    assert settings.snmp_communities == ("public",)


def test_from_env_icmp_privileged_reads_false_in_the_spellings_apphost_uses() -> None:
    for spelling in ("false", "FALSE", "0", "no"):
        assert Settings.from_env(
            REQUIRED | {"DISCOVERY_ICMP_PRIVILEGED": spelling}).icmp_privileged is False


def test_from_env_a_non_positive_interval_is_refused_rather_than_clamped() -> None:
    with pytest.raises(ValueError, match="at least 1"):
        Settings.from_env(REQUIRED | {"DISCOVERY_INTERVAL_SECONDS": "0"})
