"""What an SNMP target is, and the two operations a scan performs against one."""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from typing import Protocol

SnmpValue = str

DEFAULT_PORT = 161


class SnmpError(Exception):
    """
    An agent did not answer, or refused the request.

    Expected rather than exceptional during a scan: most addresses in a range have no SNMP agent at
    all, and the first community tried is usually the wrong one. It is caught per address and per
    community, and never reaches the cycle.
    """


@dataclass(frozen=True, slots=True)
class SnmpTarget:
    """
    Everything needed to talk to one agent.

    v2c only, and deliberately: a scan tries a list of community strings against a stranger, which
    is a thing v2c can express and USM cannot — v3 needs a user, an auth protocol and a key per
    device, which is exactly the per-device configuration that only exists once a device is known.
    A device that answers only v3 is discovered by its ping and its open ports, with no identity,
    and gets one in WP-4.2 when somebody attaches a credential to it.
    """

    host: str
    community: str
    port: int = DEFAULT_PORT
    timeout_seconds: float = 2.0
    #: No retries. The poller retries once because a dropped reading leaves a gap in a chart; a
    #: scan re-runs on its own schedule, and one retry against every silent address in a /24
    #: doubles the sweep for nothing.
    retries: int = 0


class SnmpTransport(Protocol):
    """
    The two SNMP operations a scan performs, as a protocol rather than pysnmp directly.

    Everything above it — which OIDs an identify reads, how an LLDP index becomes a local port,
    what a missing row means — is then testable without a device, a socket or a capability.
    """

    async def get(self, target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]: ...

    async def walk(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]: ...


def default_transport() -> SnmpTransport:
    # Imported here so the pure logic above and in `identify.py` is importable without pysnmp, and
    # so the library's own import cost is paid by a scan that identifies rather than by every
    # process.
    from .pysnmp_transport import PySnmpTransport

    return PySnmpTransport()
