"""The poller's client for the endpoints on the platform API that are its own."""

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

    async def fetch_credential_scope(self) -> dict[str, Any]:
        """
        Which credentials this poller's checks need, and what version each is at.

        Deliberately cheap: no material, no grant, and no row written on the server, so it is safe
        to ask every cycle. It is what makes a rotation visible without the platform having to push
        anything — the version moves and the next cycle notices.
        """
        response = await self._http.get(
            f"{self._settings.api_base_url}/api/pollers/{self._settings.name}/credentials",
            headers=await self._auth_header(),
        )
        if response.status_code == httpx.codes.NOT_FOUND:
            raise PollerNotRegisteredError(f"Poller {self._settings.name} is not registered.")
        response.raise_for_status()
        return dict(response.json())

    async def request_credential_grant(self) -> dict[str, Any]:
        """
        Mints a single-use grant over that same scope.

        The poller names nothing here: the platform derives the scope from this poller's own
        devices, so there is no request this poller can make that widens what it may read.
        """
        response = await self._http.post(
            f"{self._settings.api_base_url}/api/pollers/{self._settings.name}/credential-grants",
            headers=await self._auth_header(),
        )
        if response.status_code == httpx.codes.NOT_FOUND:
            raise PollerNotRegisteredError(f"Poller {self._settings.name} is not registered.")
        response.raise_for_status()
        return dict(response.json())

    async def fetch_runbook_executions(self) -> dict[str, Any]:
        """
        Collects the remediations waiting for this poller's group, if any.

        A *fetch*, deliberately, and it is the whole reason there is no queue: ARCHITECTURE §4 gives
        this process publish-only bus credentials and says pollers never consume commands. So the
        platform decides what should run and this asks for it, on the same cycle and under the same
        service account as the configuration read beside it.

        Claiming is the server's business — a row handed over here is already marked as this
        poller's, so two pollers sharing a group cannot both run the same remediation.
        """
        response = await self._http.get(
            f"{self._settings.api_base_url}/api/pollers/{self._settings.name}/runbook-executions",
            headers=await self._auth_header(),
        )
        if response.status_code == httpx.codes.NOT_FOUND:
            raise PollerNotRegisteredError(f"Poller {self._settings.name} is not registered.")
        response.raise_for_status()
        return dict(response.json())

    async def report_runbook_result(
        self, execution_id: str, result: dict[str, Any]
    ) -> dict[str, Any] | None:
        """
        Reports what a runbook did.

        A 409 is not an error: it means the platform already recorded a terminal state for this
        execution — its own timeout swept it while the runbook was still running. The first terminal
        state is the true one, so this returns None and the agent stops asking rather than arguing.
        """
        response = await self._http.post(
            f"{self._settings.api_base_url}/api/pollers/{self._settings.name}"
            f"/runbook-executions/{execution_id}/results",
            json=result,
            headers=await self._auth_header(),
        )
        if response.status_code == httpx.codes.CONFLICT:
            return None
        response.raise_for_status()
        return dict(response.json())

    async def redeem_credential_grant(self, grant_id: str, token: str) -> dict[str, Any]:
        """
        Spends a grant and returns the material it covers.

        The one response in this client that carries secrets. It is handed straight to
        `CredentialStore` and never logged, and the grant it spent is dead the moment this returns —
        a retry needs a new one rather than replaying this.
        """
        response = await self._http.post(
            f"{self._settings.api_base_url}/api/credential-grants/redemptions",
            json={"grantId": grant_id, "token": token},
            headers=await self._auth_header(),
        )
        response.raise_for_status()
        return dict(response.json())

    async def _auth_header(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {await self.access_token()}"}
