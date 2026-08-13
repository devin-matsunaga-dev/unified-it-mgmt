"""Configuration, read from the environment exactly once, at start-up."""

from __future__ import annotations

import os
from collections.abc import Mapping, Sequence
from dataclasses import dataclass

DEFAULT_DISCOVERED_EXCHANGE = "Contracts.Events:DeviceDiscovered"

#: Tried in order against every address that answers, until one of them replies. Overridden by
#: AppHost with the simulator's two profiles; `public` is the SNMP equivalent of trying the front
#: door, and an estate where it works is an estate with a problem this scan will find.
DEFAULT_SNMP_COMMUNITIES = ("public",)


class MissingSettingError(RuntimeError):
    """A required environment variable was absent or empty."""

    def __init__(self, name: str) -> None:
        super().__init__(f"Required environment variable {name} is not set.")
        self.name = name


@dataclass(frozen=True, slots=True)
class Settings:
    """
    Everything the discovery service needs to run.

    Read once and frozen, for the reason the poller's are: what this service scans is meant to
    change through scan profiles it fetches, and a process that also reconfigured itself from the
    environment mid-flight would have two sources of truth and one log.
    """

    name: str
    discovery_group: str
    agent_version: str
    interval_seconds: int
    api_base_url: str
    oidc_token_url: str
    oidc_client_id: str
    oidc_client_secret: str
    amqp_url: str
    discovered_exchange: str
    http_timeout_seconds: float
    max_concurrent_probes: int
    icmp_privileged: bool
    snmp_communities: tuple[str, ...]

    @classmethod
    def from_env(cls, environ: Mapping[str, str] | None = None) -> Settings:
        """
        Builds the settings, raising on the first missing value.

        Crashing here is deliberate and is the poller's rule: a scanner that starts without
        credentials and discovers it an hour later looks healthy in every dashboard while finding
        nothing, which is indistinguishable from an estate with nothing new on it.
        """
        env = os.environ if environ is None else environ
        return cls(
            name=_required(env, "DISCOVERY_NAME"),
            discovery_group=env.get("DISCOVERY_GROUP") or "default",
            agent_version=env.get("DISCOVERY_AGENT_VERSION") or "0.0.0",
            # How often it wakes to see what is due, not how often it scans: each profile carries
            # its own interval, exactly as a check does.
            interval_seconds=_positive_int(env, "DISCOVERY_INTERVAL_SECONDS", 30),
            api_base_url=_required(env, "DISCOVERY_API_BASE_URL").rstrip("/"),
            oidc_token_url=_required(env, "DISCOVERY_OIDC_TOKEN_URL"),
            oidc_client_id=_required(env, "DISCOVERY_OIDC_CLIENT_ID"),
            oidc_client_secret=_required(env, "DISCOVERY_OIDC_CLIENT_SECRET"),
            amqp_url=_required(env, "DISCOVERY_AMQP_URL"),
            discovered_exchange=(
                env.get("DISCOVERY_DISCOVERED_EXCHANGE") or DEFAULT_DISCOVERED_EXCHANGE),
            http_timeout_seconds=float(env.get("DISCOVERY_HTTP_TIMEOUT_SECONDS") or "10"),
            # Higher than the poller's check concurrency, because these are single packets against
            # addresses that mostly do not answer: a /24 at fifty in flight is five timeouts deep.
            max_concurrent_probes=_positive_int(env, "DISCOVERY_MAX_CONCURRENT_PROBES", 256),
            # Same arrangement as the poller — see its ICMP note. AppHost sets this false.
            icmp_privileged=(env.get("DISCOVERY_ICMP_PRIVILEGED") or "true").strip().casefold()
            not in ("false", "0", "no"),
            # Comma-separated and ordered. These are the only secrets this service holds, and it
            # holds them because a scan meets devices that are not monitored yet: there is no check
            # for the vault to scope a credential to, so there is nothing for WP-3.11 to release.
            # Anything found here is *identified*, never polled — the credential a device is
            # monitored with is still the vault's, and this service cannot reach the vault at all.
            snmp_communities=_communities(env.get("DISCOVERY_SNMP_COMMUNITIES")),
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


def _communities(raw: str | None) -> tuple[str, ...]:
    if not raw:
        return DEFAULT_SNMP_COMMUNITIES
    parsed: Sequence[str] = [item.strip() for item in raw.split(",") if item.strip()]
    return tuple(parsed) if parsed else DEFAULT_SNMP_COMMUNITIES
