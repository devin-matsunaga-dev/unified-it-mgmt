"""The real SNMP transport. Everything pysnmp-shaped lives here and nowhere else."""

from __future__ import annotations

from collections.abc import Sequence
from typing import Any

from pysnmp.hlapi.v3arch.asyncio import (
    CommunityData,
    ContextData,
    ObjectIdentity,
    ObjectType,
    SnmpEngine,
    UdpTransportTarget,
    UsmUserData,
    bulk_walk_cmd,
    get_cmd,
    usm3DESEDEPrivProtocol,
    usmAesCfb128Protocol,
    usmAesCfb192Protocol,
    usmAesCfb256Protocol,
    usmDESPrivProtocol,
    usmHMAC128SHA224AuthProtocol,
    usmHMAC192SHA256AuthProtocol,
    usmHMAC256SHA384AuthProtocol,
    usmHMAC384SHA512AuthProtocol,
    usmHMACMD5AuthProtocol,
    usmHMACSHAAuthProtocol,
    usmNoAuthProtocol,
    usmNoPrivProtocol,
)

from . import CheckError
from .snmp import SnmpTarget, SnmpValue

#: v2c is `mpModel=1`; v1 is 0 and is not offered, because it has no counter64 and no error codes
#: worth reading, and the WP asks for v2c and v3.
MP_MODEL_V2C = 1

#: How many rows a bulk walk asks for per round trip. Twenty-five covers a processor or storage
#: table on almost every device in one exchange without risking fragmentation on a small MTU.
MAX_REPETITIONS = 25

AUTH_PROTOCOLS: dict[str, Any] = {
    "none": usmNoAuthProtocol,
    "md5": usmHMACMD5AuthProtocol,
    "sha": usmHMACSHAAuthProtocol,
    "sha224": usmHMAC128SHA224AuthProtocol,
    "sha256": usmHMAC192SHA256AuthProtocol,
    "sha384": usmHMAC256SHA384AuthProtocol,
    "sha512": usmHMAC384SHA512AuthProtocol,
}

PRIV_PROTOCOLS: dict[str, Any] = {
    "none": usmNoPrivProtocol,
    "des": usmDESPrivProtocol,
    "3des": usm3DESEDEPrivProtocol,
    "aes": usmAesCfb128Protocol,
    "aes192": usmAesCfb192Protocol,
    "aes256": usmAesCfb256Protocol,
}


class PySnmpTransport:
    """
    Talks to a real agent.

    A fresh `SnmpEngine` per operation. It is not free, but an engine holds the v3 discovery state
    for the agents it has spoken to, and sharing one across devices makes a failure on one device
    depend on what another did — which is exactly the coupling the "one dead device never blocks the
    cycle" rule exists to prevent.
    """

    async def get(self, target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]:
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

    async def walk(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]:
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
                    # neighbouring OID anyway would otherwise put a disk row in the processor table.
                    if name.startswith(f"{root}."):
                        values[name[len(root) + 1:]] = value
        finally:
            engine.close_dispatcher()
        return values


def _auth_data(target: SnmpTarget) -> CommunityData | UsmUserData:
    if target.version == "2c":
        return CommunityData(target.community, mpModel=MP_MODEL_V2C)
    return UsmUserData(
        target.security_name,
        authKey=target.auth_key or None,
        privKey=target.priv_key or None,
        authProtocol=AUTH_PROTOCOLS[target.auth_protocol],
        privProtocol=PRIV_PROTOCOLS[target.priv_protocol],
    )


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

    `errorIndication` is a transport or engine failure — no answer, bad credential, unknown user.
    `errorStatus` is the agent refusing the request it did answer. Neither raises on its own, and a
    caller that only checked one would read "no such object" as an empty result.
    """
    where = f"{target.host}:{target.port}"
    if error_indication:
        raise CheckError(f"SNMP request to {where} failed: {error_indication}")
    if error_status:
        raise CheckError(
            f"The agent at {where} refused the request: {error_status.prettyPrint()} "
            f"at index {error_index}.")


def _read(var_binds: Any) -> dict[str, SnmpValue]:
    values: dict[str, SnmpValue] = {}
    for name, value in var_binds:
        # `prettyPrint` is pysnmp's own rendering: an OCTET STRING becomes text, a Counter64 becomes
        # digits, and an OID becomes dotted notation. Everything above this reads strings and
        # converts what it expects to be numeric, so nothing depends on pysnmp's type objects.
        values[str(name)] = str(value.prettyPrint())
    return values
