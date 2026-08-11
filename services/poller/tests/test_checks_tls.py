from __future__ import annotations

import asyncio
import ssl
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

import pytest
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

from poller.checks import CheckError
from poller.checks.tls import TlsCheck, is_tls


def certificate(expires_in_days: float, common_name: str = "mail.example.test") -> bytes:
    """
    A self-signed certificate expiring when the test says, in DER form.

    Built rather than committed as a fixture, because every assertion here is about the distance
    between a date on a certificate and now — a fixture would start passing for the wrong reason and
    then, on the day it expired, fail for one.
    """
    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(x509.oid.NameOID.COMMON_NAME, common_name)])
    now = datetime.now(UTC)
    built = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(days=365))
        .not_valid_after(now + timedelta(days=expires_in_days))
        .sign(key, hashes.SHA256())
    )
    return built.public_bytes(serialization.Encoding.DER)


def serving(der: bytes) -> Any:
    calls: list[tuple[str, int, str, float]] = []

    async def fetch(host: str, port: int, server_name: str, timeout_seconds: float) -> bytes:
        calls.append((host, port, server_name, timeout_seconds))
        return der

    fetch.calls = calls  # type: ignore[attr-defined]
    return fetch


def failing(error: Exception) -> Any:
    async def fetch(host: str, port: int, server_name: str, timeout_seconds: float) -> bytes:
        raise error

    return fetch


def days_of(outcome: Any) -> float:
    return float(next(
        metric.value for metric in outcome.metrics if metric.name == "tls.days_to_expiry"))


async def test_run_a_certificate_with_time_left_reports_the_days_remaining() -> None:
    outcome = await TlsCheck(serving(certificate(45))).run(
        "mail.example.test", {"port": "443"}, timeout_seconds=5)

    assert outcome.succeeded
    assert days_of(outcome) == pytest.approx(45, abs=0.01)


async def test_run_an_expired_certificate_is_a_measurement_of_a_negative_number() -> None:
    outcome = await TlsCheck(serving(certificate(-3))).run(
        "mail.example.test", {"port": "443"}, timeout_seconds=5)

    # Not a failed check. A failure advances the availability rule, and the days figure — the number
    # the threshold is actually set on — would never reach the alert engine at all.
    assert outcome.succeeded
    assert days_of(outcome) == pytest.approx(-3, abs=0.01)


async def test_run_sends_the_device_address_as_the_server_name_by_default() -> None:
    fetch = serving(certificate(10))

    await TlsCheck(fetch).run("10.0.0.5", {"port": "8443"}, timeout_seconds=7)

    assert fetch.calls == [("10.0.0.5", 8443, "10.0.0.5", 7)]


async def test_run_a_server_name_parameter_overrides_the_address() -> None:
    fetch = serving(certificate(10))

    await TlsCheck(fetch).run(
        "10.0.0.5", {"port": "443", "serverName": "portal.example.test"}, timeout_seconds=5)

    # One address can serve several certificates, and which one is returned depends on the name the
    # client asked for. Without this, a check against an IP reads whichever is the default.
    assert fetch.calls[0][2] == "portal.example.test"


async def test_run_without_a_port_parameter_is_refused() -> None:
    with pytest.raises(CheckError) as raised:
        await TlsCheck(serving(certificate(10))).run("mail.example.test", {}, timeout_seconds=5)

    assert "'port'" in str(raised.value)


async def test_run_a_handshake_that_times_out_names_the_budget() -> None:
    with pytest.raises(CheckError) as raised:
        await TlsCheck(failing(TimeoutError())).run(
            "mail.example.test", {"port": "443"}, timeout_seconds=3)

    assert "did not complete within 3s" in str(raised.value)


async def test_run_a_refused_connection_raises_naming_the_listener() -> None:
    with pytest.raises(CheckError) as raised:
        await TlsCheck(failing(ConnectionRefusedError("Connection refused"))).run(
            "mail.example.test", {"port": "443"}, timeout_seconds=5)

    assert "mail.example.test:443" in str(raised.value)
    assert "Connection refused" in str(raised.value)


async def test_run_a_handshake_that_returned_no_certificate_is_refused() -> None:
    with pytest.raises(CheckError) as raised:
        await TlsCheck(serving(b"")).run("mail.example.test", {"port": "443"}, timeout_seconds=5)

    # Reporting zero days here would raise an expiry alert about a certificate nobody was served.
    assert "without sending a certificate" in str(raised.value)


async def test_run_bytes_that_are_not_a_certificate_are_refused_rather_than_crashing() -> None:
    with pytest.raises(CheckError) as raised:
        await TlsCheck(serving(b"not a certificate")).run(
            "mail.example.test", {"port": "443"}, timeout_seconds=5)

    assert "could not be read" in str(raised.value)


async def test_run_reports_no_text_metrics() -> None:
    outcome = await TlsCheck(serving(certificate(30))).run(
        "mail.example.test", {"port": "443"}, timeout_seconds=5)

    # WP-3.4 keys a text metric by (device, metric name), so an issuer or subject fact from two TLS
    # checks on one device would overwrite each other and name the wrong certificate.
    assert all(metric.text is None for metric in outcome.metrics)


async def test_fetch_certificate_reads_a_self_signed_certificate_off_a_real_listener(
    tmp_path: Path,
) -> None:
    """
    The load-bearing claim in this module, proved against a real handshake.

    `getpeercert()` returns an empty dictionary when `verify_mode` is `CERT_NONE`, and the whole
    check depends on the *binary* form being returned anyway — otherwise the one certificate an
    expiry check exists for, an untrusted or already-expired one, could not be read at all.
    """
    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(x509.oid.NameOID.COMMON_NAME, "local.example.test")])
    now = datetime.now(UTC)
    built = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(days=1))
        .not_valid_after(now + timedelta(days=17))
        .sign(key, hashes.SHA256())
    )
    chain = tmp_path / "server.pem"
    chain.write_bytes(
        built.public_bytes(serialization.Encoding.PEM)
        + key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8,
            serialization.NoEncryption(),
        ))

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(chain)
    server = await asyncio.start_server(
        lambda reader, writer: writer.close(), "127.0.0.1", 0, ssl=context)
    port = int(server.sockets[0].getsockname()[1])

    async with server:
        outcome = await TlsCheck().run(
            "127.0.0.1", {"port": str(port), "serverName": "local.example.test"},
            timeout_seconds=5)

    assert outcome.succeeded
    assert days_of(outcome) == pytest.approx(17, abs=0.01)


def test_is_tls_matches_the_type_the_api_spells_case_insensitively() -> None:
    assert is_tls({"type": "Tls"})
    assert not is_tls({"type": "Tcp"})
