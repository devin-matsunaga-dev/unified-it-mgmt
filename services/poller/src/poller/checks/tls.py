"""TLS: how many days the certificate a listener serves has left."""

from __future__ import annotations

import asyncio
import ssl
from collections.abc import Awaitable, Callable, Mapping
from datetime import UTC, datetime
from typing import Any

from cryptography import x509

from . import CheckError, CheckOutcome, Metric, describe, parameter_int

SECONDS_PER_DAY = 86_400.0

#: Fetches the certificate a listener serves, as DER bytes. A function rather than a class so the
#: parsing and the arithmetic above it are testable against a certificate built in the test, with no
#: socket, no listener and no clock skew.
CertificateFetcher = Callable[[str, int, str, float], Awaitable[bytes]]


class TlsCheck:
    """
    Completes a TLS handshake and reports the days remaining on the certificate it was served.

    Two things this deliberately does *not* do.

    It does not verify the certificate. A self-signed or already-expired certificate is exactly the
    case somebody wants a number for, and a verifying handshake fails before it can produce one —
    the check would go from "expires in 3 days" to "the check is broken" on the day it mattered.
    This check answers "when does this expire", never "is this trusted"; one that answered both
    would have no way to say which of the two had failed.

    It reports a certificate that has already expired as a *successful* measurement of a negative
    number, following the ICMP check's rule that 100% packet loss is a measurement rather than a
    failure. A failed check advances the availability rule instead, and the days figure — the thing
    the threshold is set on — would never reach the alert engine at all.
    """

    def __init__(self, fetch: CertificateFetcher | None = None) -> None:
        self._fetch = fetch

    async def run(
        self,
        address: str,
        parameters: Mapping[str, str],
        timeout_seconds: float,
    ) -> CheckOutcome:
        port = parameter_int(parameters, "port", 0, minimum=1, maximum=65535)
        if port == 0:
            raise CheckError("A TLS check needs a 'port' parameter naming the port to connect to.")

        # Which name to ask for, when one address serves several certificates. Defaults to the
        # device's own address, which is what a client with nothing else to go on sends.
        server_name = (parameters.get("serverName") or "").strip() or address

        fetch = self._fetch if self._fetch is not None else fetch_certificate
        try:
            der = await fetch(address, port, server_name, timeout_seconds)
        except CheckError:
            raise
        except TimeoutError as error:
            raise CheckError(
                f"TLS handshake with {address}:{port} did not complete within "
                f"{timeout_seconds:g}s.") from error
        except Exception as error:  # one listener's failure, not the cycle's
            raise CheckError(
                f"TLS handshake with {address}:{port} failed: {describe(error)}") from error

        expires_at = _expiry_of(der, address, port)
        days = (expires_at - datetime.now(UTC)).total_seconds() / SECONDS_PER_DAY

        # Numbers only, no issuer or subject text. WP-3.4 stores a text metric as a fact keyed by
        # (device, metric name), so two TLS checks on one device — a web listener and a mail one —
        # would overwrite each other's issuer and leave a field that names the wrong certificate.
        return CheckOutcome(
            succeeded=True,
            metrics=(Metric("tls.days_to_expiry", value=days, unit="d"),),
        )


def _expiry_of(der: bytes, address: str, port: int) -> datetime:
    if not der:
        # A handshake that completed without a peer certificate. Nothing to measure, and reporting
        # zero days would raise an expiry alert about a certificate nobody was served.
        raise CheckError(
            f"{address}:{port} completed a TLS handshake without sending a certificate.")

    try:
        certificate = x509.load_der_x509_certificate(der)
    except Exception as error:
        raise CheckError(
            f"The certificate served by {address}:{port} could not be read: "
            f"{describe(error)}") from error

    return certificate.not_valid_after_utc


async def fetch_certificate(
    host: str,
    port: int,
    server_name: str,
    timeout_seconds: float,
) -> bytes:
    """
    Opens a TLS connection and returns the peer's certificate in DER form.

    `binary_form=True` is what makes an unverified handshake usable: `getpeercert()` returns an
    empty dictionary when `verify_mode` is `CERT_NONE`, while the DER bytes come back either way.
    """
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE

    _, writer = await asyncio.wait_for(
        asyncio.open_connection(host, port, ssl=context, server_hostname=server_name),
        timeout=timeout_seconds,
    )
    try:
        ssl_object = writer.get_extra_info("ssl_object")
        if ssl_object is None:
            raise CheckError(f"The connection to {host}:{port} did not negotiate TLS.")
        der: bytes | None = ssl_object.getpeercert(binary_form=True)
        return der or b""
    finally:
        writer.close()
        try:
            await writer.wait_closed()
        except OSError:
            # The certificate is already read; how the peer hung up is not the check's news.
            pass


def is_tls(check: Mapping[str, Any]) -> bool:
    return str(check.get("type") or "").casefold() == "tls"
