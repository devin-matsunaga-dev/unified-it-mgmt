from __future__ import annotations

from typing import Any

import httpx
import pytest

from poller.checks import CheckError
from poller.checks.http import MAX_BODY_BYTES, HttpCheck, is_http

URL = "http://mailhog:8025/"


def responding(
    status_code: int = 200,
    body: bytes = b"<html>MailHog</html>",
    headers: dict[str, str] | None = None,
) -> httpx.MockTransport:
    def handle(request: httpx.Request) -> httpx.Response:
        requests.append(request)
        return httpx.Response(status_code, content=body, headers=headers)

    requests: list[httpx.Request] = []
    transport = httpx.MockTransport(handle)
    transport.requests = requests  # type: ignore[attr-defined]
    return transport


def raising(error: Exception) -> httpx.MockTransport:
    def handle(request: httpx.Request) -> httpx.Response:
        raise error

    return httpx.MockTransport(handle)


def metrics_of(outcome: Any) -> dict[str, float | None]:
    return {metric.name: metric.value for metric in outcome.metrics}


async def run(
    transport: httpx.MockTransport,
    parameters: dict[str, str] | None = None,
    timeout_seconds: float = 5,
) -> Any:
    return await HttpCheck(transport).run(
        "mailhog", {"url": URL} | (parameters or {}), timeout_seconds=timeout_seconds)


async def test_run_a_page_that_answers_200_succeeds_and_reports_status_size_and_latency() -> None:
    outcome = await run(responding())

    assert outcome.succeeded
    metrics = metrics_of(outcome)
    assert metrics["http.status_code"] == 200.0
    assert metrics["http.response_bytes"] == float(len(b"<html>MailHog</html>"))
    assert metrics["http.response_ms"] == outcome.latency_ms


@pytest.mark.parametrize("status_code", [301, 404, 500])
async def test_run_a_status_outside_2xx_fails_by_default(status_code: int) -> None:
    outcome = await run(responding(status_code))

    assert not outcome.succeeded
    assert str(status_code) in (outcome.error or "")
    # The reading still travelled: latency during an outage is the part of a chart people read.
    assert metrics_of(outcome)["http.status_code"] == float(status_code)


async def test_run_an_expected_status_replaces_the_2xx_rule_in_both_directions() -> None:
    # A service whose health endpoint answers 401 to an unauthenticated probe is up, and a check
    # that says so is more useful than one that has to be pointed somewhere less interesting.
    assert (await run(responding(401), {"expectedStatus": "401"})).succeeded

    refused = await run(responding(200), {"expectedStatus": "401"})
    assert not refused.succeeded
    assert "not the expected 401" in (refused.error or "")


async def test_run_expected_content_present_succeeds_and_absent_fails() -> None:
    assert (await run(responding(), {"expectedContent": "MailHog"})).succeeded

    outcome = await run(responding(), {"expectedContent": "Grafana"})
    assert not outcome.succeeded
    assert "'Grafana'" in (outcome.error or "")


async def test_run_a_wrong_status_is_named_before_the_content_it_could_not_contain() -> None:
    outcome = await run(responding(503, b""), {"expectedContent": "MailHog"})

    # "answered 503" is the actionable half; "and the body does not contain MailHog" is noise on a
    # page that was never served.
    assert "503" in (outcome.error or "")
    assert "MailHog" not in (outcome.error or "")


async def test_run_undecodable_bytes_are_matched_rather_than_failing_the_check() -> None:
    outcome = await run(responding(body=b"\xff\xfeMailHog"), {"expectedContent": "MailHog"})

    assert outcome.succeeded


async def test_run_reads_no_more_than_the_body_cap() -> None:
    outcome = await run(responding(body=b"x" * (MAX_BODY_BYTES + 5_000)))

    assert metrics_of(outcome)["http.response_bytes"] == float(MAX_BODY_BYTES)


async def test_run_does_not_follow_a_redirect_unless_asked() -> None:
    transport = responding(302, b"", {"location": "http://mailhog:8025/login"})

    outcome = await run(transport)

    assert not outcome.succeeded
    assert "302" in (outcome.error or "")


async def test_run_follows_a_redirect_when_asked_to() -> None:
    seen: list[str] = []

    def handle(request: httpx.Request) -> httpx.Response:
        seen.append(str(request.url))
        if request.url.path == "/":
            return httpx.Response(302, headers={"location": "http://mailhog:8025/inbox"})
        return httpx.Response(200, content=b"inbox")

    outcome = await run(httpx.MockTransport(handle), {"followRedirects": "true"})

    assert outcome.succeeded
    assert seen == ["http://mailhog:8025/", "http://mailhog:8025/inbox"]


async def test_run_uses_the_method_it_is_given() -> None:
    transport = responding()

    await run(transport, {"method": "head"})

    assert transport.requests[0].method == "HEAD"  # type: ignore[attr-defined]


async def test_run_a_method_this_check_does_not_perform_is_refused() -> None:
    with pytest.raises(CheckError) as raised:
        await run(responding(), {"method": "DELETE"})

    assert "GET, HEAD" in str(raised.value)


async def test_run_without_a_url_is_refused() -> None:
    with pytest.raises(CheckError) as raised:
        await HttpCheck(responding()).run("mailhog", {}, timeout_seconds=5)

    assert "'url'" in str(raised.value)


@pytest.mark.parametrize("status", ["99", "600", "ok"])
async def test_run_an_expected_status_that_is_not_a_status_is_refused(status: str) -> None:
    with pytest.raises(CheckError):
        await run(responding(), {"expectedStatus": status})


async def test_run_a_timeout_names_the_budget_it_exceeded() -> None:
    with pytest.raises(CheckError) as raised:
        await run(raising(httpx.ConnectTimeout("timed out")), timeout_seconds=2)

    assert "did not answer within 2s" in str(raised.value)


async def test_run_a_connection_failure_raises_with_the_libraries_own_reason() -> None:
    with pytest.raises(CheckError) as raised:
        await run(raising(httpx.ConnectError("[Errno 111] Connection refused")))

    assert "Connection refused" in str(raised.value)
    assert URL in str(raised.value)


def test_is_http_matches_the_type_the_api_spells_case_insensitively() -> None:
    assert is_http({"type": "Http"})
    assert not is_http({"type": "Tls"})
