from __future__ import annotations

import httpx
import pytest

from poller.api import ConfigVersionRejectedError, PlatformApiClient, PollerNotRegisteredError
from tests.test_agent import SETTINGS


def client(handler: httpx.MockTransport) -> PlatformApiClient:
    return PlatformApiClient(SETTINGS, httpx.AsyncClient(transport=handler))


def token_response() -> httpx.Response:
    return httpx.Response(200, json={"access_token": "a-token", "expires_in": 300})


async def test_access_token_is_reused_until_it_is_close_to_expiry() -> None:
    calls: list[httpx.Request] = []

    def handle(request: httpx.Request) -> httpx.Response:
        calls.append(request)
        return token_response()

    api = client(httpx.MockTransport(handle))

    assert await api.access_token(now=0.0) == "a-token"
    assert await api.access_token(now=100.0) == "a-token"
    # 300s lifetime less the 30s margin: the token is refetched at 270, not at 300.
    assert await api.access_token(now=280.0) == "a-token"

    assert len(calls) == 2


async def test_fetch_config_sends_the_bearer_token_and_the_version() -> None:
    seen: list[httpx.Request] = []

    def handle(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        if request.url.path.endswith("/token"):
            return token_response()
        return httpx.Response(200, json={"configVersion": 7, "isFullSnapshot": False})

    api = client(httpx.MockTransport(handle))
    await api.fetch_config(4)

    config_request = seen[-1]
    assert config_request.url.path == "/api/pollers/poller-1/config"
    assert config_request.url.params["sinceVersion"] == "4"
    assert config_request.headers["Authorization"] == "Bearer a-token"


async def test_fetch_config_without_a_version_asks_for_a_snapshot() -> None:
    seen: list[httpx.Request] = []

    def handle(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        if request.url.path.endswith("/token"):
            return token_response()
        return httpx.Response(200, json={"configVersion": 7, "isFullSnapshot": True})

    api = client(httpx.MockTransport(handle))
    await api.fetch_config(None)

    assert "sinceVersion" not in seen[-1].url.params


@pytest.mark.parametrize(
    ("status", "expected"),
    [(400, ConfigVersionRejectedError), (404, PollerNotRegisteredError)],
)
async def test_fetch_config_distinguishes_a_bad_version_from_an_unknown_poller(
    status: int, expected: type[Exception]
) -> None:
    def handle(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/token"):
            return token_response()
        return httpx.Response(status, json={"title": "no"})

    api = client(httpx.MockTransport(handle))

    with pytest.raises(expected):
        await api.fetch_config(99)


async def test_register_posts_the_pollers_own_name_and_group() -> None:
    seen: list[httpx.Request] = []

    def handle(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        if request.url.path.endswith("/token"):
            return token_response()
        return httpx.Response(200, json={"name": "poller-1"})

    api = client(httpx.MockTransport(handle))
    await api.register()

    assert seen[-1].url.path == "/api/pollers/registrations"
    assert b'"pollerGroup":"default"' in seen[-1].content.replace(b" ", b"")


async def test_fetch_config_unauthorized_is_raised_rather_than_swallowed() -> None:
    def handle(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/token"):
            return token_response()
        return httpx.Response(403, json={"title": "Forbidden"})

    api = client(httpx.MockTransport(handle))

    # A 403 means the credential is wrong, which is not something a retry fixes quietly — the agent
    # logs it and keeps the configuration it holds, but the client must not pretend it succeeded.
    with pytest.raises(httpx.HTTPStatusError):
        await api.fetch_config(None)
