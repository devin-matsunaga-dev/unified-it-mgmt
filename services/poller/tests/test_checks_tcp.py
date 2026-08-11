from __future__ import annotations

import asyncio
from collections.abc import AsyncIterator

import pytest

from poller.checks import CheckError
from poller.checks.tcp import TcpCheck, is_tcp


@pytest.fixture
async def listener() -> AsyncIterator[int]:
    """A real listener on loopback: a connect check is not worth faking the connect out of."""

    async def handle(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        writer.close()

    server = await asyncio.start_server(handle, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    async with server:
        yield int(port)


async def closed_port() -> int:
    """A port nothing is listening on: bound, read, and released before the check runs."""
    server = await asyncio.start_server(lambda r, w: None, "127.0.0.1", 0)
    port = int(server.sockets[0].getsockname()[1])
    server.close()
    await server.wait_closed()
    return port


def metrics_of(outcome: object) -> dict[str, float | None]:
    return {metric.name: metric.value for metric in outcome.metrics}  # type: ignore[attr-defined]


async def test_run_a_port_that_accepts_succeeds_and_reports_the_connect_time(listener: int) -> None:
    outcome = await TcpCheck().run("127.0.0.1", {"port": str(listener)}, timeout_seconds=5)

    assert outcome.succeeded
    assert outcome.latency_ms is not None
    assert metrics_of(outcome)["tcp.connect_ms"] == outcome.latency_ms


async def test_run_a_port_nothing_is_listening_on_raises_naming_the_address() -> None:
    port = await closed_port()

    with pytest.raises(CheckError) as raised:
        await TcpCheck().run("127.0.0.1", {"port": str(port)}, timeout_seconds=5)

    assert f"127.0.0.1:{port}" in str(raised.value)


async def test_run_without_a_port_parameter_is_refused() -> None:
    with pytest.raises(CheckError) as raised:
        await TcpCheck().run("127.0.0.1", {}, timeout_seconds=5)

    assert "'port'" in str(raised.value)


@pytest.mark.parametrize("port", ["0", "65536", "smtp"])
async def test_run_a_port_that_is_not_a_port_is_refused(port: str) -> None:
    with pytest.raises(CheckError):
        await TcpCheck().run("127.0.0.1", {"port": port}, timeout_seconds=5)


async def test_run_a_host_that_does_not_resolve_raises_rather_than_hanging() -> None:
    with pytest.raises(CheckError) as raised:
        await TcpCheck().run(
            "no-such-host.invalid", {"port": "25"}, timeout_seconds=5)

    # `.invalid` is reserved by RFC 2606 and never resolves, so this is a DNS failure by
    # construction rather than one that depends on what the machine's resolver happens to answer.
    assert "no-such-host.invalid:25" in str(raised.value)


async def test_run_a_connect_that_outlives_the_timeout_is_reported_as_a_timeout() -> None:
    # 198.51.100.0/24 (RFC 5737) is routed nowhere, so the connect neither completes nor is
    # refused — it hangs, which is the case the wait_for exists for.
    with pytest.raises(CheckError) as raised:
        await TcpCheck().run("198.51.100.1", {"port": "25"}, timeout_seconds=0.2)

    assert "did not complete within" in str(raised.value)


def test_is_tcp_matches_the_type_the_api_spells_case_insensitively() -> None:
    assert is_tcp({"type": "Tcp"})
    assert not is_tcp({"type": "Http"})
    assert not is_tcp({})
