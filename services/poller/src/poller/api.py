"""The poller's client for its own two endpoints on the platform API."""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any

import httpx

from .settings import Settings

# Renew a little before expiry rather than on it, so a token never dies mid-request.
TOKEN_EXPIRY_MARGIN_SECONDS = 30


class ConfigVersionRejectedError(RuntimeError):
    """
    The server refused the version the poller asked from.

    It answers 400 when `sinceVersion` is ahead of its own — a poller holding a version this server
    never issued is reading a restored or foreign database. The recovery is to forget the version
    and ask for a full snapshot, not to retry the same question.
    """


class PollerNotRegisteredError(RuntimeError):
    """The config fetch found no such poller. Registration is retried on the next cycle."""


@dataclass(slots=True)
class _CachedToken:
    value: str
    expires_at: float


class PlatformApiClient:
    """
    Talks to the platform as the poller's own service account.

    The token is client-credentials: there is no user here and no browser flow, which is why the
    poller's Keycloak client has neither enabled.
    """

    def __init__(self, settings: Settings, http: httpx.AsyncClient) -> None:
        self._settings = settings
        self._http = http
        self._token: _CachedToken | None = None

    async def access_token(self, now: float | None = None) -> str:
        """Returns a cached token, fetching a new one only once the old is close to expiry."""
        moment = time.monotonic() if now is None else now
        cached = self._token
        if cached is not None and cached.expires_at > moment:
            return cached.value

        response = await self._http.post(
            self._settings.oidc_token_url,
            data={
                "grant_type": "client_credentials",
                "client_id": self._settings.oidc_client_id,
                "client_secret": self._settings.oidc_client_secret,
            },
            headers={"Content-Type": "application/x-www-form-urlencoded"},
        )
        response.raise_for_status()
        payload = response.json()
        token = str(payload["access_token"])
        lifetime = float(payload.get("expires_in", 60))
        self._token = _CachedToken(
            value=token,
            expires_at=moment + max(lifetime - TOKEN_EXPIRY_MARGIN_SECONDS, 1.0),
        )
        return token

    async def register(self) -> dict[str, Any]:
        """
        Announces this poller. Registration is an upsert on the name, so it is safe — and
        deliberate — to do it on every start-up and after every failure.
        """
        response = await self._http.post(
            f"{self._settings.api_base_url}/api/pollers/registrations",
            json={
                "name": self._settings.name,
                "pollerGroup": self._settings.poller_group,
                "agentVersion": self._settings.agent_version,
            },
            headers=await self._auth_header(),
        )
        response.raise_for_status()
        return dict(response.json())

    async def fetch_config(self, since_version: int | None) -> dict[str, Any]:
        """
        Fetches a full snapshot (`since_version` None or 0) or the delta since a version.

        The 400 and the 404 mean different things and are raised as different errors: one says the
        poller's idea of history is wrong, the other that the platform has never heard of it.
        """
        params = {} if not since_version else {"sinceVersion": str(since_version)}
        response = await self._http.get(
            f"{self._settings.api_base_url}/api/pollers/{self._settings.name}/config",
            params=params,
            headers=await self._auth_header(),
        )
        if response.status_code == httpx.codes.BAD_REQUEST:
            raise ConfigVersionRejectedError(
                f"The server rejected sinceVersion={since_version}: {response.text}"
            )
        if response.status_code == httpx.codes.NOT_FOUND:
            raise PollerNotRegisteredError(f"Poller {self._settings.name} is not registered.")
        response.raise_for_status()
        return dict(response.json())

    async def _auth_header(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {await self.access_token()}"}
