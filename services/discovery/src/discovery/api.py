"""The discovery service's client for its one endpoint on the platform API."""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any

import httpx

from .settings import Settings

# Renew a little before expiry rather than on it, so a token never dies mid-request.
TOKEN_EXPIRY_MARGIN_SECONDS = 30


@dataclass(slots=True)
class _CachedToken:
    value: str
    expires_at: float


class PlatformApiClient:
    """
    Talks to the platform as the discovery service's own service account.

    One endpoint, and that is the whole surface: `GET /api/discovery/{group}/scan-profiles`. There
    is no registration and no heartbeat — unlike a poller, this service holds no configuration
    version the server has to track, and nothing downstream needs to know it is late. A group that
    has never been written a profile is answered with an empty list rather than a 404, so the first
    cycle of a freshly deployed scanner is not an error.
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

    async def fetch_scan_profiles(self) -> dict[str, Any]:
        """
        What this group has to scan, sent whole every time.

        No `sinceVersion` and no delta: a group has a handful of profiles, and a scanner that
        re-reads a short list costs nothing while one that misses a change scans the wrong range
        for an hour. The service names only its own group, so there is no request it can make that
        widens the ranges it may probe.
        """
        response = await self._http.get(
            f"{self._settings.api_base_url}/api/discovery/"
            f"{self._settings.discovery_group}/scan-profiles",
            headers={"Authorization": f"Bearer {await self.access_token()}"},
        )
        response.raise_for_status()
        return dict(response.json())
