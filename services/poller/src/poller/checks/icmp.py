"""ICMP reachability: the only check that answers "is it there at all"."""

from __future__ import annotations

from collections.abc import Awaitable, Callable, Mapping
from typing import Any, Protocol

from . import CheckError, CheckOutcome, Metric, describe, parameter_int

#: Packets per check. Three is enough to distinguish a lossy link from a dead one without making a
#: reachability probe the slowest thing in the cycle.
DEFAULT_COUNT = 3

#: Seconds between packets. Deliberately far below icmplib's own default of one second: a check with
#: a five-second timeout that spends three of them waiting to send has no timeout budget left.
PACKET_INTERVAL_SECONDS = 0.2


class PingHost(Protocol):
    """The part of `icmplib.Host` this module reads."""

    @property
    def is_alive(self) -> bool: ...

    @property
    def avg_rtt(self) -> float: ...

    @property
    def packet_loss(self) -> float: ...

    @property
    def packets_sent(self) -> int: ...

    @property
    def packets_received(self) -> int: ...


PingFunction = Callable[..., Awaitable[PingHost]]


def _default_ping() -> PingFunction:
    # Imported lazily so that the module is importable — and every test in this file runs — on a
    # machine without the capability icmplib needs to open its socket.
    from icmplib import async_ping

    return async_ping  # type: ignore[no-any-return]


class IcmpCheck:
    """
    Pings a device and reports whether it answered, how quickly, and how much it dropped.

    `privileged` controls which socket icmplib opens: a raw socket, which needs `CAP_NET_RAW` in the
    process's *effective* set, or an ICMP datagram socket, which needs only a
    `net.ipv4.ping_group_range` covering the process's group.

    The container runs as a non-root user and takes the second path. `--cap-add=NET_RAW` puts the
    capability in the permitted set, and a non-root process without a file capability on its binary
    has an empty effective set — so the raw socket fails there with "Root privileges are required"
    however the container is launched. Granting one uid the right to ping is also narrower than
    granting the container raw-socket access to the network.
    """

    def __init__(self, ping: PingFunction | None = None, privileged: bool = True) -> None:
        self._ping = ping
        self._privileged = privileged

    async def run(
        self,
        address: str,
        parameters: Mapping[str, str],
        timeout_seconds: float,
    ) -> CheckOutcome:
        count = parameter_int(parameters, "count", DEFAULT_COUNT, minimum=1, maximum=10)

        # icmplib's timeout is per packet, while the check's is for the whole thing. Dividing keeps
        # a slow check inside the budget the operator set; the floor stops a three-packet check with
        # a one-second timeout from giving each packet 333ms and calling a live device dead.
        per_packet_timeout = max(timeout_seconds / count, 1.0)

        ping = self._ping if self._ping is not None else _default_ping()
        try:
            host = await ping(
                address,
                count=count,
                interval=PACKET_INTERVAL_SECONDS,
                timeout=per_packet_timeout,
                privileged=self._privileged,
            )
        except Exception as error:  # every failure here is one device's failure
            raise CheckError(f"Ping to {address} failed: {describe(error)}") from error

        metrics = [
            Metric("icmp.packet_loss_percent", value=float(host.packet_loss) * 100, unit="%"),
            Metric("icmp.packets_sent", value=float(host.packets_sent)),
            Metric("icmp.packets_received", value=float(host.packets_received)),
        ]
        if host.is_alive:
            metrics.insert(0, Metric("icmp.rtt_ms", value=float(host.avg_rtt), unit="ms"))
            return CheckOutcome(
                succeeded=True,
                latency_ms=float(host.avg_rtt),
                metrics=tuple(metrics),
            )

        # A device that answered nothing is still a successful measurement of an unreachable device:
        # the loss figure is the fact. It reports as a failure so reachability can read it.
        return CheckOutcome(
            succeeded=False,
            error=f"No reply from {address} after {host.packets_sent} packets.",
            metrics=tuple(metrics),
        )


def is_icmp(check: Mapping[str, Any]) -> bool:
    return str(check.get("type") or "").casefold() == "icmp"
