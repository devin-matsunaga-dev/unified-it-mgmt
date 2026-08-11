"""TCP connect: is anything accepting connections on that port."""

from __future__ import annotations

import asyncio
import time
from collections.abc import Mapping
from typing import Any

from . import CheckError, CheckOutcome, Metric, describe, parameter_int

#: Set explicitly rather than defaulted, so a check against the wrong port is a configuration error
#: an operator can see rather than a service that looks down. `CheckRules` requires it too.
PORT = "port"


class TcpCheck:
    """
    Opens a TCP connection to a port and closes it again.

    Deliberately nothing more: a connect proves a listener is accepting, which is the whole question
    a port check asks. Sending a probe and reading a banner would make the check protocol-specific,
    and the protocol-specific one is the HTTP check next door.

    The connection is always closed, including when the check times out. A poller that leaked a
    socket per cycle would exhaust its file descriptors in a day and report every device down.
    """

    async def run(
        self,
        address: str,
        parameters: Mapping[str, str],
        timeout_seconds: float,
    ) -> CheckOutcome:
        port = parameter_int(parameters, PORT, 0, minimum=1, maximum=65535)
        if port == 0:
            raise CheckError(
                f"A TCP check needs a '{PORT}' parameter naming the port to connect to.")

        started = time.monotonic()
        try:
            _, writer = await asyncio.wait_for(
                asyncio.open_connection(address, port), timeout=timeout_seconds)
        except TimeoutError as error:
            raise CheckError(
                f"TCP connect to {address}:{port} did not complete within {timeout_seconds:g}s."
            ) from error
        except OSError as error:
            # Refused, unreachable, or a name that does not resolve. All are one fact — nothing is
            # accepting there — and the operating system's own words are the most useful reason.
            raise CheckError(
                f"TCP connect to {address}:{port} failed: {describe(error)}") from error

        connect_ms = (time.monotonic() - started) * 1000
        await _close(writer)
        return CheckOutcome(
            succeeded=True,
            latency_ms=connect_ms,
            metrics=(Metric("tcp.connect_ms", value=connect_ms, unit="ms"),),
        )


async def _close(writer: asyncio.StreamWriter) -> None:
    writer.close()
    try:
        await writer.wait_closed()
    except OSError:
        # The connection this check just proved was open is now closed one way or another, which is
        # what was wanted. A peer that resets rather than closes cleanly is not a failed check.
        pass


def is_tcp(check: Mapping[str, Any]) -> bool:
    return str(check.get("type") or "").casefold() == "tcp"
