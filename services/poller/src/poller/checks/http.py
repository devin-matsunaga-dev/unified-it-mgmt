"""HTTP(S): does the service answer, with the status and the words it should, and how fast."""

from __future__ import annotations

import time
from collections.abc import Mapping
from typing import Any

import httpx

from . import CheckError, CheckOutcome, Metric, describe, parameter_int

#: How much of a response body is read before the check stops. A monitoring agent must not pull a
#: gigabyte because somebody pointed a check at a download; a page's own text is in the first
#: part of it, and a content expectation is matched against what was read.
MAX_BODY_BYTES = 1_048_576

#: Read rather than assumed, so the check measures what a browser would experience.
DEFAULT_METHOD = "GET"
METHODS = ("GET", "HEAD")


class HttpCheck:
    """
    Requests a URL and judges the answer against what the check says it should be.

    Three expectations, each optional and each independently a failure: the request completed, the
    status matched (any 2xx unless `expectedStatus` names one), and the body contained
    `expectedContent`. A failure names which of the three it was — "the site is down" and "the site
    is serving the wrong page" are different call-outs at three in the morning.

    Redirects are not followed unless asked. A service check that follows a 302 to a login page and
    then matches nothing is reporting on a page nobody asked about, and the redirect itself is
    usually the fact worth alerting on.
    """

    def __init__(self, transport: httpx.AsyncBaseTransport | None = None) -> None:
        # Injected for tests, which use `httpx.MockTransport` and so exercise the real client, the
        # real timeout handling and the real streaming path without a socket.
        self._transport = transport

    async def run(
        self,
        address: str,
        parameters: Mapping[str, str],
        timeout_seconds: float,
    ) -> CheckOutcome:
        url = (parameters.get("url") or "").strip()
        if not url:
            raise CheckError("An HTTP check needs a 'url' parameter naming what to request.")

        method = (parameters.get("method") or DEFAULT_METHOD).strip().upper()
        if method not in METHODS:
            raise CheckError(
                f"Parameter 'method' must be one of {', '.join(METHODS)}; got '{method}'.")

        expected_status = _expected_status(parameters)
        expected_content = (parameters.get("expectedContent") or "").strip()
        follow_redirects = _flag(parameters, "followRedirects")

        started = time.monotonic()
        try:
            async with httpx.AsyncClient(
                transport=self._transport,
                timeout=timeout_seconds,
                follow_redirects=follow_redirects,
            ) as client:
                async with client.stream(method, url) as response:
                    body = await _read_capped(response)
        except httpx.TimeoutException as error:
            raise CheckError(
                f"{method} {url} did not answer within {timeout_seconds:g}s.") from error
        except httpx.HTTPError as error:
            # Connect refused, DNS, TLS — one fact, and httpx's own words are the useful reason.
            raise CheckError(f"{method} {url} failed: {describe(error)}") from error

        latency_ms = (time.monotonic() - started) * 1000
        metrics = (
            Metric("http.status_code", value=float(response.status_code)),
            Metric("http.response_bytes", value=float(len(body)), unit="B"),
            Metric("http.response_ms", value=latency_ms, unit="ms"),
        )

        # A response that arrived is a measurement, so the metrics travel whether or not the
        # expectations were met: latency during an outage is the part of the chart people read.
        if problem := _expectation_problem(
            response.status_code, body, url, method, expected_status, expected_content):
            return CheckOutcome(
                succeeded=False, latency_ms=latency_ms, error=problem, metrics=metrics)

        return CheckOutcome(succeeded=True, latency_ms=latency_ms, metrics=metrics)


def _expectation_problem(
    status_code: int,
    body: bytes,
    url: str,
    method: str,
    expected_status: int | None,
    expected_content: str,
) -> str | None:
    if expected_status is not None:
        if status_code != expected_status:
            return f"{method} {url} answered {status_code}, not the expected {expected_status}."
    elif not 200 <= status_code < 300:
        return f"{method} {url} answered {status_code}."

    if expected_content:
        # Decoded permissively: a check that failed because a page had one byte of broken UTF-8 in a
        # footer would be reporting on the encoding rather than on the service.
        text = body.decode("utf-8", errors="replace")
        if expected_content not in text:
            return (
                f"{method} {url} answered {status_code} but its body does not contain "
                f"'{expected_content}'.")

    return None


def _expected_status(parameters: Mapping[str, str]) -> int | None:
    """
    The one status code the check requires, or None for "any 2xx".

    One code rather than a list or a class, matching `CheckRules.HttpProblem` on the server: the two
    have to agree exactly, across two languages, and every form the rule can take is a form they can
    come to disagree about.
    """
    raw = parameters.get("expectedStatus")
    if raw is None or raw.strip() == "":
        return None
    return parameter_int(parameters, "expectedStatus", 0, minimum=100, maximum=599)


def _flag(parameters: Mapping[str, str], name: str) -> bool:
    return (parameters.get(name) or "").strip().casefold() in ("true", "1", "yes")


async def _read_capped(response: httpx.Response) -> bytes:
    body = bytearray()
    async for chunk in response.aiter_bytes():
        body.extend(chunk)
        if len(body) >= MAX_BODY_BYTES:
            return bytes(body[:MAX_BODY_BYTES])
    return bytes(body)


def is_http(check: Mapping[str, Any]) -> bool:
    return str(check.get("type") or "").casefold() == "http"
