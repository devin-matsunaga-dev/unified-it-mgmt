"""Configuration, read from the environment exactly once, at start-up."""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass

DEFAULT_HEARTBEAT_EXCHANGE = "Contracts.Events:PollerHeartbeat"


class MissingSettingError(RuntimeError):
    """A required environment variable was absent or empty."""

    def __init__(self, name: str) -> None:
        super().__init__(f"Required environment variable {name} is not set.")
        self.name = name


@dataclass(frozen=True, slots=True)
class Settings:
    """
    Everything the poller needs to run.

    Read once and frozen: a poller that reconfigured itself mid-flight would make its own logs
    unreadable, and the platform already has a mechanism for changing what it does — the versioned
    config it fetches every cycle.
    """

    name: str
    poller_group: str
    agent_version: str
    interval_seconds: int
    api_base_url: str
    oidc_token_url: str
    oidc_client_id: str
    oidc_client_secret: str
    amqp_url: str
    heartbeat_exchange: str
    http_timeout_seconds: float

    @classmethod
    def from_env(cls, environ: Mapping[str, str] | None = None) -> Settings:
        """
        Build the settings, raising on the first missing value.

        Crashing here is deliberate: a poller that starts without credentials and discovers it an
        hour later looks healthy in every dashboard while doing nothing.
        """
        env = os.environ if environ is None else environ
        return cls(
            name=_required(env, "POLLER_NAME"),
            poller_group=env.get("POLLER_GROUP") or "default",
            agent_version=env.get("POLLER_AGENT_VERSION") or "0.0.0",
            interval_seconds=_positive_int(env, "POLLER_INTERVAL_SECONDS", 15),
            api_base_url=_required(env, "POLLER_API_BASE_URL").rstrip("/"),
            oidc_token_url=_required(env, "POLLER_OIDC_TOKEN_URL"),
            oidc_client_id=_required(env, "POLLER_OIDC_CLIENT_ID"),
            oidc_client_secret=_required(env, "POLLER_OIDC_CLIENT_SECRET"),
            amqp_url=_required(env, "POLLER_AMQP_URL"),
            heartbeat_exchange=env.get("POLLER_HEARTBEAT_EXCHANGE") or DEFAULT_HEARTBEAT_EXCHANGE,
            http_timeout_seconds=float(env.get("POLLER_HTTP_TIMEOUT_SECONDS") or "10"),
        )


def _required(env: Mapping[str, str], name: str) -> str:
    value = env.get(name)
    if not value:
        raise MissingSettingError(name)
    return value


def _positive_int(env: Mapping[str, str], name: str, default: int) -> int:
    raw = env.get(name)
    if not raw:
        return default
    value = int(raw)
    if value < 1:
        raise ValueError(f"{name} must be at least 1, got {value}.")
    return value
