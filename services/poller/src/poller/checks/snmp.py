"""SNMP v2c and v3: system inventory, processor load, memory use and interface traffic."""

from __future__ import annotations

import time
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Any, Protocol

from . import CheckError, CheckOutcome, Metric, describe, interfaces, parameter_int
from . import oids as oid

SnmpValue = str | int | float

#: What the check's `metric` parameter may ask for. `oid` reads whatever the operator names, which
#: is the escape hatch for a device whose vendor MIB nobody has taught this poller.
SYS_INFO = "sysinfo"
CPU = "cpu"
MEMORY = "memory"
INTERFACES = "interfaces"
RAW_OID = "oid"
METRICS = (SYS_INFO, CPU, MEMORY, INTERFACES, RAW_OID)

DEFAULT_PORT = 161
DEFAULT_COMMUNITY = "public"

#: v3 protocol names as an operator writes them, mapped to pysnmp's objects on use. Spelled without
#: hyphens or case so "SHA-256", "sha256" and "Sha256" are one answer.
AUTH_PROTOCOLS = ("none", "md5", "sha", "sha224", "sha256", "sha384", "sha512")
PRIV_PROTOCOLS = ("none", "des", "3des", "aes", "aes192", "aes256")


@dataclass(frozen=True, slots=True)
class SnmpTarget:
    """Everything needed to talk to one agent, read out of the check's parameters."""

    host: str
    port: int
    version: str
    community: str
    security_name: str
    auth_protocol: str
    auth_key: str
    priv_protocol: str
    priv_key: str
    timeout_seconds: float
    retries: int


class SnmpTransport(Protocol):
    """
    The two SNMP operations this poller performs.

    A protocol rather than pysnmp directly, so that everything above it — which OIDs a metric reads,
    how a processor table becomes one number, what a missing row means — is testable without a
    device, a socket or a capability.
    """

    async def get(self, target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]: ...

    async def walk(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]: ...


class SnmpCheck:
    """
    Reads one metric family from one agent.

    Which family is the check's `metric` parameter, so a device carries an "SNMP: CPU" check and an
    "SNMP: memory" check with their own intervals and thresholds rather than one check that returns
    everything at whichever interval the busiest metric needs.
    """

    def __init__(self, transport: SnmpTransport | None = None) -> None:
        self._transport = transport
        # One cache for every interface check this poller runs, because a rate is a subtraction
        # between two cycles and the check object is the only thing that spans them. Keyed by agent
        # inside, so two devices never subtract one another's counters.
        self._interfaces = interfaces.InterfaceRateCache()

    async def run(
        self,
        address: str,
        parameters: Mapping[str, str],
        timeout_seconds: float,
    ) -> CheckOutcome:
        target = build_target(address, parameters, timeout_seconds)
        metric = (parameters.get("metric") or SYS_INFO).strip().casefold()
        if metric not in METRICS:
            raise CheckError(
                f"Parameter 'metric' must be one of {', '.join(METRICS)}; got '{metric}'.")

        transport = self._transport if self._transport is not None else _default_transport()
        started = time.monotonic()
        try:
            if metric == SYS_INFO:
                metrics = await _read_sys_info(transport, target)
            elif metric == CPU:
                metrics = await _read_cpu(transport, target)
            elif metric == MEMORY:
                metrics = await _read_memory(transport, target)
            elif metric == INTERFACES:
                metrics = await self._read_interfaces(transport, target)
            else:
                metrics = await _read_raw_oid(transport, target, parameters)
        except CheckError:
            raise
        except Exception as error:  # one agent's failure, not the cycle's
            raise CheckError(
                f"SNMP {metric} against {target.host}:{target.port} failed: {describe(error)}"
            ) from error

        latency_ms = (time.monotonic() - started) * 1000
        if not metrics:
            # The agent answered, but not with this. Reporting success with nothing in it would
            # read on a chart as a device with zero CPU rather than one that does not report CPU.
            return CheckOutcome.failure(
                f"The agent at {target.host} returned no {metric} values.", latency_ms=latency_ms)

        return CheckOutcome(
            succeeded=True, latency_ms=latency_ms, metrics=tuple(metrics))

    async def _read_interfaces(
        self,
        transport: SnmpTransport,
        target: SnmpTarget,
    ) -> list[Metric]:
        """
        Every interface the device has, with the rates its counters imply since the last cycle.

        The clock is read once, before either walk, and used as the timestamp of both: a big table
        takes a moment to walk and dividing by "when the second walk finished" would attribute that
        moment's traffic to the wrong interval on every cycle.
        """
        at = time.monotonic()
        if_table = await transport.walk(target, oid.IF_TABLE)
        # An older access switch has no ifXTable at all. Its absence costs the 64-bit counters and
        # the alias, both of which have an ifTable fallback, so it is an empty result rather than a
        # failed check.
        if_x_table = await transport.walk(target, oid.IF_X_TABLE)

        readings = interfaces.read_table(if_table, if_x_table)
        return interfaces.measure(
            readings, self._interfaces, f"{target.host}:{target.port}", at)


def build_target(
    address: str,
    parameters: Mapping[str, str],
    timeout_seconds: float,
) -> SnmpTarget:
    """
    Turns the check's free-text parameters into a target, refusing what SNMP cannot express.

    Since WP-3.11 the credential fields — `community` for v2c, `securityName`/`authKey`/`privKey`
    for v3 — normally arrive here because `CredentialStore.apply` merged them over the check's
    stored parameters a moment ago, not because anybody typed them into the check. This function is
    deliberately unchanged by that: it reads a mapping either way, which is what lets a check with
    no credential keep working exactly as it did. The defaults below are the fallback for that case,
    and `DEFAULT_COMMUNITY` is what an unauthenticated SNMP check actually tries.
    """
    version = (parameters.get("version") or "2c").strip().casefold()
    if version not in ("2c", "3"):
        raise CheckError(f"Parameter 'version' must be '2c' or '3'; got '{version}'.")

    auth_protocol = _protocol(parameters, "authProtocol", AUTH_PROTOCOLS)
    priv_protocol = _protocol(parameters, "privProtocol", PRIV_PROTOCOLS)
    security_name = (parameters.get("securityName") or "").strip()
    auth_key = parameters.get("authKey") or ""
    priv_key = parameters.get("privKey") or ""

    if version == "3":
        if not security_name:
            raise CheckError("SNMP v3 needs a 'securityName' parameter.")
        if auth_protocol != "none" and not auth_key:
            raise CheckError(f"SNMP v3 auth protocol '{auth_protocol}' needs an 'authKey'.")
        if priv_protocol != "none" and not priv_key:
            raise CheckError(f"SNMP v3 priv protocol '{priv_protocol}' needs a 'privKey'.")
        if priv_protocol != "none" and auth_protocol == "none":
            # SNMP has no privacy without authentication; the USM security levels are noAuthNoPriv,
            # authNoPriv and authPriv, and the fourth combination does not exist.
            raise CheckError("SNMP v3 privacy requires authentication; set 'authProtocol'.")

    return SnmpTarget(
        host=address,
        port=parameter_int(parameters, "port", DEFAULT_PORT, minimum=1, maximum=65535),
        version=version,
        community=parameters.get("community") or DEFAULT_COMMUNITY,
        security_name=security_name,
        auth_protocol=auth_protocol,
        auth_key=auth_key,
        priv_protocol=priv_protocol,
        priv_key=priv_key,
        timeout_seconds=timeout_seconds,
        # One retry, not pysnmp's default of five: the scheduler decides how often a device is
        # asked, and five retries inside one check turns a five-second timeout into thirty.
        retries=parameter_int(parameters, "retries", 1, minimum=0, maximum=5),
    )


def _protocol(parameters: Mapping[str, str], name: str, allowed: Sequence[str]) -> str:
    raw = (parameters.get(name) or "none").strip().casefold().replace("-", "").replace("_", "")
    if raw not in allowed:
        raise CheckError(f"Parameter '{name}' must be one of {', '.join(allowed)}; got '{raw}'.")
    return raw


async def _read_sys_info(transport: SnmpTransport, target: SnmpTarget) -> list[Metric]:
    """
    The device's own description of itself. Text, apart from uptime.

    Every field is optional: an agent that answers sysDescr and nothing else is common, and refusing
    the lot because sysLocation was never configured would be a poller that reports nothing about
    most of the estate.
    """
    values = await transport.get(target, [
        oid.SYS_DESCR, oid.SYS_NAME, oid.SYS_LOCATION, oid.SYS_CONTACT,
        oid.SYS_OBJECT_ID, oid.SYS_UPTIME,
    ])

    metrics: list[Metric] = []
    for name, source in (
        ("system.description", oid.SYS_DESCR),
        ("system.name", oid.SYS_NAME),
        ("system.location", oid.SYS_LOCATION),
        ("system.contact", oid.SYS_CONTACT),
        ("system.object_id", oid.SYS_OBJECT_ID),
    ):
        text = values.get(source)
        if text is not None and str(text).strip():
            metrics.append(Metric(name, text=str(text).strip()))

    uptime = values.get(oid.SYS_UPTIME)
    if uptime is not None:
        metrics.append(Metric(
            "system.uptime_seconds",
            value=_as_float(uptime, "sysUpTime") / oid.TIMETICKS_PER_SECOND,
            unit="s",
        ))
    return metrics


async def _read_cpu(transport: SnmpTransport, target: SnmpTarget) -> list[Metric]:
    """
    Processor load, averaged across cores, from host-resources — or net-snmp's idle counter.

    Averaging is a choice: a four-core host with one core pinned reads 25%, which is what "the CPU"
    means to somebody looking at a device tile. The per-core figures travel too, so a future chart
    can show the spread rather than re-deriving it.
    """
    loads = await transport.walk(target, oid.HR_PROCESSOR_LOAD)
    if loads:
        # Named by the device's own table index rather than by position, so a core keeps its name
        # across polls even if the agent reorders the table or a processor drops out of it.
        per_core = {index: _as_float(value, index) for index, value in loads.items()}
        metrics = [Metric(
            "cpu.utilisation_percent",
            value=sum(per_core.values()) / len(per_core),
            unit="%",
        )]
        metrics.extend(
            Metric(f"cpu.core_{index}_percent", value=percentage, unit="%")
            for index, percentage in sorted(per_core.items())
        )
        return metrics

    idle = (await transport.get(target, [oid.UCD_CPU_IDLE])).get(oid.UCD_CPU_IDLE)
    if idle is None:
        return []
    return [Metric(
        "cpu.utilisation_percent",
        value=100 - _as_float(idle, "ssCpuIdle"),
        unit="%",
    )]


async def _read_memory(transport: SnmpTransport, target: SnmpTarget) -> list[Metric]:
    """
    Physical memory from the host-resources storage table, falling back to net-snmp's counters.

    The storage table holds disks and buffers beside RAM, so the type column picks the rows out;
    a host with several memory rows (NUMA nodes are reported this way) has them summed.
    """
    types = await transport.walk(target, oid.HR_STORAGE_TYPE)
    ram_indices = [
        index for index, value in types.items()
        if str(value).strip() == oid.HR_STORAGE_RAM
    ]
    if ram_indices:
        units = await transport.walk(target, oid.HR_STORAGE_ALLOCATION_UNITS)
        sizes = await transport.walk(target, oid.HR_STORAGE_SIZE)
        used = await transport.walk(target, oid.HR_STORAGE_USED)

        total_bytes = 0.0
        used_bytes = 0.0
        for index in ram_indices:
            if index not in sizes or index not in used:
                continue
            unit = _as_float(units.get(index, 1), "hrStorageAllocationUnits")
            total_bytes += _as_float(sizes[index], "hrStorageSize") * unit
            used_bytes += _as_float(used[index], "hrStorageUsed") * unit
        if total_bytes > 0:
            return _memory_metrics(total_bytes, used_bytes)

    fallback = await transport.get(
        target, [oid.UCD_MEMORY_TOTAL_REAL, oid.UCD_MEMORY_AVAILABLE_REAL])
    total = fallback.get(oid.UCD_MEMORY_TOTAL_REAL)
    available = fallback.get(oid.UCD_MEMORY_AVAILABLE_REAL)
    if total is None or available is None:
        return []

    # UCD reports kilobytes; everything this poller publishes is in bytes so a chart never has to
    # know which MIB a number came from.
    total_bytes = _as_float(total, "memTotalReal") * 1024
    if total_bytes <= 0:
        return []
    return _memory_metrics(total_bytes, total_bytes - _as_float(available, "memAvailReal") * 1024)


def _memory_metrics(total_bytes: float, used_bytes: float) -> list[Metric]:
    return [
        Metric("memory.used_percent", value=used_bytes / total_bytes * 100, unit="%"),
        Metric("memory.used_bytes", value=used_bytes, unit="B"),
        Metric("memory.total_bytes", value=total_bytes, unit="B"),
    ]


async def _read_raw_oid(
    transport: SnmpTransport,
    target: SnmpTarget,
    parameters: Mapping[str, str],
) -> list[Metric]:
    """One OID the operator named, for a vendor MIB this poller has never heard of."""
    requested = (parameters.get("oid") or "").strip()
    if not requested:
        raise CheckError("Metric 'oid' needs an 'oid' parameter naming what to read.")

    name = (parameters.get("metricName") or "").strip() or f"snmp.{requested}"
    values = await transport.get(target, [requested])
    if requested not in values:
        return []

    value = values[requested]
    # A raw OID may be a counter or a string, and the operator naming it is not asked to say which.
    try:
        return [Metric(name, value=_as_float(value, requested), unit=parameters.get("unit"))]
    except CheckError:
        return [Metric(name, text=str(value))]


def _as_float(value: SnmpValue, what: str) -> float:
    try:
        return float(value)
    except (TypeError, ValueError) as error:
        raise CheckError(f"{what} returned '{value}', which is not a number.") from error


def _default_transport() -> SnmpTransport:
    # Imported here so the pure logic above is importable without pysnmp installed, and so the
    # library's own import cost is paid by a poller that has an SNMP check rather than by every one.
    from .pysnmp_transport import PySnmpTransport

    return PySnmpTransport()


def is_snmp(check: Mapping[str, Any]) -> bool:
    return str(check.get("type") or "").casefold() == "snmp"
