"""The real SNMP transport. Everything pysnmp-shaped lives here and nowhere else."""

from __future__ import annotations

import asyncio
from collections.abc import Sequence
from typing import Any

from pysnmp.hlapi.v3arch.asyncio import (
    CommunityData,
    ContextData,
    ObjectIdentity,
    ObjectType,
    SnmpEngine,
    UdpTransportTarget,
    bulk_walk_cmd,
    get_cmd,
)

from .snmp import SnmpError, SnmpTarget, SnmpValue

#: v2c is `mpModel=1`. v1 is 0 and is not offered: a scan that fell back to v1 would report an
#: identity from a protocol with no error codes worth reading.
MP_MODEL_V2C = 1

#: How many rows a bulk walk asks for per round trip. Twenty-five covers a neighbour table on
#: almost every device in one exchange without risking fragmentation on a small MTU.
MAX_REPETITIONS = 25


class PySnmpTransport:
    """
    Talks to a real agent.

    Every call is wrapped in :func:`asyncio.to_thread`, which the poller's equivalent does not do
    and should: pysnmp performs its BER/ASN.1 work synchronously on the event loop, and WP-3.8's
    hand-verification measured a single SNMP check stalling the loop for 35-81 ms. A poller runs a
    handful of checks a cycle and merely mis-measures its own latency; a scan runs an identify
    against every address that answered, so the same stall would serialise the whole sweep behind
    the parsing — and the sweep's concurrency is the only thing making a /24 finish in seconds.

    A fresh `SnmpEngine` per operation, for the poller's reason: an engine holds per-agent state,
    and sharing one across addresses makes a failure on one depend on what another did.
    """

    async def get(self, target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]:
        return await asyncio.to_thread(self._get, target, list(requested))

    async def walk(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]:
        return await asyncio.to_thread(self._walk, target, root)

    def _get(self, target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]:
        return asyncio.run(self._get_async(target, requested))

    def _walk(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]:
        return asyncio.run(self._walk_async(target, root))

    async def _get_async(
        self,
        target: SnmpTarget,
        requested: Sequence[str],
    ) -> dict[str, SnmpValue]:
        engine = SnmpEngine()
        try:
            error_indication, error_status, error_index, var_binds = await get_cmd(
                engine,
                _auth_data(target),
                await _transport_target(target),
                ContextData(),
                *[ObjectType(ObjectIdentity(item)) for item in requested],
            )
            _raise_for(target, error_indication, error_status, error_index)
            return _read(var_binds)
        finally:
            engine.close_dispatcher()

    async def _walk_async(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]:
        engine = SnmpEngine()
        values: dict[str, SnmpValue] = {}
        try:
            walk = bulk_walk_cmd(
                engine,
                _auth_data(target),
                await _transport_target(target),
                ContextData(),
                0,
                MAX_REPETITIONS,
                ObjectType(ObjectIdentity(root)),
                lexicographicMode=False,
            )
            async for error_indication, error_status, error_index, var_binds in walk:
                _raise_for(target, error_indication, error_status, error_index)
                for name, value in _read(var_binds).items():
                    # The walk is asked to stop at the subtree, but a device that answers a
                    # neighbouring OID anyway would otherwise put an interface row in the LLDP
                    # table.
                    if name.startswith(f"{root}."):
                        values[name[len(root) + 1:]] = value
        finally:
            engine.close_dispatcher()
        return values


def _auth_data(target: SnmpTarget) -> CommunityData:
    return CommunityData(target.community, mpModel=MP_MODEL_V2C)


async def _transport_target(target: SnmpTarget) -> UdpTransportTarget:
    return await UdpTransportTarget.create(
        (target.host, target.port),
        timeout=target.timeout_seconds,
        retries=target.retries,
    )


def _raise_for(
    target: SnmpTarget,
    error_indication: Any,
    error_status: Any,
    error_index: Any,
) -> None:
    """
    Turns pysnmp's two error channels into one exception.

    `errorIndication` is a transport or engine failure — no answer, wrong community. `errorStatus`
    is the agent refusing a request it did answer. Neither raises on its own, and a caller that
    checked only one would read "no such object" as an empty result and report a device with no
    identity.
    """
    where = f"{target.host}:{target.port}"
    if error_indication:
        raise SnmpError(f"SNMP request to {where} failed: {error_indication}")
    if error_status:
        raise SnmpError(
            f"The agent at {where} refused the request: {error_status.prettyPrint()} "
            f"at index {error_index}.")


def _read(var_binds: Any) -> dict[str, SnmpValue]:
    values: dict[str, SnmpValue] = {}
    for name, value in var_binds:
        values[str(name)] = render(value)
    return values


def render(value: Any) -> str:
    """
    One var-bind value as a string, with OIDs kept numeric.

    `prettyPrint` is pysnmp's own rendering and is right for almost everything: an OCTET STRING
    becomes text and a Counter64 becomes digits, so nothing above this depends on pysnmp's type
    objects. It is wrong for an OID. `prettyPrint` resolves one against whatever MIB modules happen
    to be installed beside this process, so `sysObjectID` comes back as
    `SNMPv2-SMI::enterprises.8072.3.2.10` where a module covers it and as `1.3.6.1.4.1.8072.3.2.10`
    where none does — the *format* of the field then depends on the scanner's Python environment
    rather than on the device.

    That matters because sysObjectID is the vendor-and-model fingerprint WP-4.2 will match a
    discovery against a CI with, and a key that renders two ways for one device is not a key. Found
    by this package's own hand-verification, against a live simulator, after the tests had passed
    with the numeric form in the fixture.
    """
    as_tuple = getattr(value, "asTuple", None)
    if callable(as_tuple):
        try:
            parts = as_tuple()
        except Exception:
            parts = None
        # Only an OID answers `asTuple()` with a tuple of plain integers; an OCTET STRING has
        # `asNumbers()` and an INTEGER has neither, so nothing else is caught by this.
        if parts and all(isinstance(part, int) for part in parts):
            return ".".join(str(part) for part in parts)
    return str(value.prettyPrint())
