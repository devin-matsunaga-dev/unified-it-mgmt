from __future__ import annotations

import pytest

from poller.settings import DEFAULT_HEARTBEAT_EXCHANGE, MissingSettingError, Settings

COMPLETE_ENV = {
    "POLLER_NAME": "poller-1",
    "POLLER_API_BASE_URL": "http://localhost:5000/",
    "POLLER_OIDC_TOKEN_URL": "http://localhost:8080/realms/it-platform/protocol/openid-connect/token",
    "POLLER_OIDC_CLIENT_ID": "it-platform-poller",
    "POLLER_OIDC_CLIENT_SECRET": "secret",
    "POLLER_AMQP_URL": "amqp://poller:secret@localhost:5672/",
}


def test_from_env_complete_environment_applies_defaults() -> None:
    settings = Settings.from_env(dict(COMPLETE_ENV))

    assert settings.name == "poller-1"
    assert settings.poller_group == "default"
    assert settings.interval_seconds == 15
    assert settings.heartbeat_exchange == DEFAULT_HEARTBEAT_EXCHANGE
    # The trailing slash would otherwise produce "//api/pollers" on every request.
    assert settings.api_base_url == "http://localhost:5000"


def test_from_env_missing_credential_raises_naming_the_variable() -> None:
    environment = dict(COMPLETE_ENV)
    del environment["POLLER_OIDC_CLIENT_SECRET"]

    with pytest.raises(MissingSettingError) as failure:
        Settings.from_env(environment)

    assert failure.value.name == "POLLER_OIDC_CLIENT_SECRET"


def test_from_env_zero_interval_is_refused() -> None:
    with pytest.raises(ValueError, match="at least 1"):
        Settings.from_env(dict(COMPLETE_ENV) | {"POLLER_INTERVAL_SECONDS": "0"})
