from __future__ import annotations

from collections.abc import Sequence
from typing import Any

import pytest

from poller.checks import CheckError
from poller.checks import oids as oid
from poller.checks.snmp import SnmpCheck, SnmpTarget, SnmpValue, build_target


class FakeTransport:
    """
    An agent that answers whatever the test decided it answers.

    Everything worth testing about SNMP polling here is above the wire: which OIDs a metric reads,
    what a processor table averages to, which fallback a bare device falls back to, and what a
    missing row means. None of that needs a socket.
    """

    def __init__(
        self,
        scalars: dict[str, SnmpValue] | None = None,
        tables: dict[str, dict[str, SnmpValue]] | None = None,
        fail_with: Exception | None = None,
    ) -> None:
        self.scalars = scalars or {}
        self.tables = tables or {}
        self.fail_with = fail_with
        self.requested: list[str] = []
        self.walked: list[str] = []

    async def get(self, target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]:
        if self.fail_with is not None:
            raise self.fail_with
        self.requested.extend(requested)
        return {name: self.scalars[name] for name in requested if name in self.scalars}

    async def walk(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]:
        if self.fail_with is not None:
            raise self.fail_with
        self.walked.append(root)
        return dict(self.tables.get(root, {}))


def metrics_of(outcome: Any) -> dict[str, Any]:
    return {metric.name: metric.value if metric.value is not None else metric.text
            for metric in outcome.metrics}


async def run(transport: FakeTransport, **parameters: str) -> Any:
    return await SnmpCheck(transport).run("10.0.0.5", parameters, timeout_seconds=5)


# --- sysInfo -------------------------------------------------------------------------------------

async def test_run_sysinfo_reports_the_text_fields_and_uptime_in_seconds() -> None:
    transport = FakeTransport(scalars={
        oid.SYS_DESCR: "Cisco IOS Software, C2960",
        oid.SYS_NAME: "sw-hq-01",
        oid.SYS_LOCATION: "Head Office comms room",
        oid.SYS_UPTIME: "360000",
    })

    outcome = await run(transport, metric="sysinfo")

    assert outcome.succeeded
    assert metrics_of(outcome) == {
        "system.description": "Cisco IOS Software, C2960",
        "system.name": "sw-hq-01",
        "system.location": "Head Office comms room",
        # Timeticks are hundredths of a second, which is nobody's idea of a readable unit.
        "system.uptime_seconds": 3600.0,
    }


async def test_run_sysinfo_omits_fields_the_agent_left_blank() -> None:
    transport = FakeTransport(scalars={oid.SYS_DESCR: "A device", oid.SYS_LOCATION: "   "})

    outcome = await run(transport, metric="sysinfo")

    # sysLocation unconfigured is the normal state of most of an estate; a blank string on a chart
    # is worse than an absent one.
    assert "system.location" not in metrics_of(outcome)
    assert outcome.succeeded


async def test_run_sysinfo_an_agent_answering_nothing_fails_rather_than_succeeding_empty() -> None:
    outcome = await run(FakeTransport(), metric="sysinfo")

    assert not outcome.succeeded
    assert "no sysinfo values" in (outcome.error or "")


# --- CPU -----------------------------------------------------------------------------------------

async def test_run_cpu_averages_the_processor_table_and_keeps_the_per_core_figures() -> None:
    transport = FakeTransport(tables={oid.HR_PROCESSOR_LOAD: {"1": "10", "2": "30", "3": "20"}})

    outcome = await run(transport, metric="cpu")

    metrics = metrics_of(outcome)
    assert metrics["cpu.utilisation_percent"] == 20.0
    # Named by the agent's own table index, so core 2 is the row the agent calls 2.
    assert metrics["cpu.core_1_percent"] == 10.0
    assert metrics["cpu.core_2_percent"] == 30.0
    assert metrics["cpu.core_3_percent"] == 20.0


async def test_run_cpu_falls_back_to_the_net_snmp_idle_counter() -> None:
    transport = FakeTransport(scalars={oid.UCD_CPU_IDLE: "88"})

    outcome = await run(transport, metric="cpu")

    # Most appliances carry UCD-SNMP and no host-resources tables at all.
    assert metrics_of(outcome)["cpu.utilisation_percent"] == 12.0
    assert transport.walked == [oid.HR_PROCESSOR_LOAD]


async def test_run_cpu_a_device_with_neither_source_fails_rather_than_reporting_zero() -> None:
    outcome = await run(FakeTransport(), metric="cpu")

    # Zero percent CPU and "does not report CPU" are different facts, and one of them is alarming.
    assert not outcome.succeeded


# --- Memory --------------------------------------------------------------------------------------

async def test_run_memory_reads_the_ram_rows_of_the_storage_table_and_ignores_the_disks() -> None:
    transport = FakeTransport(tables={
        oid.HR_STORAGE_TYPE: {"1": oid.HR_STORAGE_RAM, "2": "1.3.6.1.2.1.25.2.1.4"},
        oid.HR_STORAGE_ALLOCATION_UNITS: {"1": "1024", "2": "4096"},
        oid.HR_STORAGE_SIZE: {"1": "8192", "2": "500000"},
        oid.HR_STORAGE_USED: {"1": "2048", "2": "400000"},
    })

    outcome = await run(transport, metric="memory")

    metrics = metrics_of(outcome)
    assert metrics["memory.total_bytes"] == 8192 * 1024
    assert metrics["memory.used_bytes"] == 2048 * 1024
    assert metrics["memory.used_percent"] == 25.0


async def test_run_memory_sums_several_ram_rows() -> None:
    transport = FakeTransport(tables={
        oid.HR_STORAGE_TYPE: {"1": oid.HR_STORAGE_RAM, "2": oid.HR_STORAGE_RAM},
        oid.HR_STORAGE_ALLOCATION_UNITS: {"1": "1024", "2": "1024"},
        oid.HR_STORAGE_SIZE: {"1": "1000", "2": "1000"},
        oid.HR_STORAGE_USED: {"1": "500", "2": "100"},
    })

    outcome = await run(transport, metric="memory")

    # NUMA nodes are reported as separate rows; a machine does not have two memories.
    assert metrics_of(outcome)["memory.used_percent"] == 30.0


async def test_run_memory_falls_back_to_the_net_snmp_counters_in_kilobytes() -> None:
    transport = FakeTransport(scalars={
        oid.UCD_MEMORY_TOTAL_REAL: "4000",
        oid.UCD_MEMORY_AVAILABLE_REAL: "1000",
    })

    outcome = await run(transport, metric="memory")

    metrics = metrics_of(outcome)
    assert metrics["memory.total_bytes"] == 4000 * 1024
    assert metrics["memory.used_percent"] == 75.0


async def test_run_memory_a_device_reporting_no_memory_at_all_fails() -> None:
    outcome = await run(FakeTransport(), metric="memory")

    assert not outcome.succeeded


# --- Raw OID -------------------------------------------------------------------------------------

async def test_run_oid_reads_what_the_operator_named_under_the_name_they_chose() -> None:
    transport = FakeTransport(scalars={"1.3.6.1.4.1.9.1.1": "42"})

    outcome = await run(
        transport, metric="oid", oid="1.3.6.1.4.1.9.1.1", metricName="fan.rpm", unit="rpm")

    assert metrics_of(outcome) == {"fan.rpm": 42.0}
    assert outcome.metrics[0].unit == "rpm"


async def test_run_oid_a_string_valued_oid_is_reported_as_text() -> None:
    transport = FakeTransport(scalars={"1.3.6.1.4.1.9.1.1": "slot empty"})

    outcome = await run(transport, metric="oid", oid="1.3.6.1.4.1.9.1.1")

    assert outcome.metrics[0].text == "slot empty"
    assert outcome.metrics[0].value is None


async def test_run_oid_without_an_oid_parameter_is_refused() -> None:
    with pytest.raises(CheckError, match="needs an 'oid' parameter"):
        await run(FakeTransport(), metric="oid")


# --- Interfaces ----------------------------------------------------------------------------------

def if_tables() -> dict[str, dict[str, SnmpValue]]:
    return {
        oid.IF_TABLE: {
            f"{oid.IF_DESCR}.1": "GigabitEthernet0/1",
            f"{oid.IF_ADMIN_STATUS}.1": "1",
            f"{oid.IF_OPER_STATUS}.1": "1",
            f"{oid.IF_IN_OCTETS}.1": "1000",
        },
        oid.IF_X_TABLE: {
            f"{oid.IF_HIGH_SPEED}.1": "1000",
            f"{oid.IF_HC_IN_OCTETS}.1": "1000",
        },
    }


async def test_run_interfaces_walks_both_tables_and_reports_each_link() -> None:
    transport = FakeTransport(tables=if_tables())

    outcome = await run(transport, metric="interfaces")

    assert outcome.succeeded
    # Two walks, not fourteen: a subtree each, from which the columns are picked out here.
    assert transport.walked == [oid.IF_TABLE, oid.IF_X_TABLE]
    assert metrics_of(outcome)["interface.1.name"] == "GigabitEthernet0/1"
    assert metrics_of(outcome)["interface.1.oper_status"] == 1.0


async def test_run_interfaces_reports_a_rate_on_the_second_cycle_of_one_check_object() -> None:
    tables = if_tables()
    transport = FakeTransport(tables=tables)
    check = SnmpCheck(transport)

    first = await check.run("10.0.0.5", {"metric": "interfaces"}, timeout_seconds=5)
    tables[oid.IF_X_TABLE][f"{oid.IF_HC_IN_OCTETS}.1"] = "99000"
    second = await check.run("10.0.0.5", {"metric": "interfaces"}, timeout_seconds=5)

    # The check object spans cycles and the counters do not: a rate exists only because the same
    # runner remembered the previous reading, which is why `SnmpCheck` is constructed once in
    # `__main__` and not per check.
    assert "interface.1.bits_in_per_second" not in metrics_of(first)
    assert metrics_of(second)["interface.1.bits_in_per_second"] > 0


async def test_run_interfaces_against_a_device_with_no_interface_table_fails_the_check() -> None:
    outcome = await run(FakeTransport(), metric="interfaces")

    # Not a success with nothing in it: a switch that answers SNMP but not IF-MIB is a check pointed
    # at the wrong thing, and an empty chart says so far less clearly than a failing check does.
    assert not outcome.succeeded
    assert "no interfaces values" in (outcome.error or "")


# --- Failures and target building ----------------------------------------------------------------

async def test_run_an_unknown_metric_is_refused_by_name() -> None:
    with pytest.raises(CheckError, match="sysinfo, cpu, memory, interfaces, oid"):
        await run(FakeTransport(), metric="temperature")


async def test_run_a_transport_failure_becomes_a_check_error_naming_the_agent() -> None:
    transport = FakeTransport(fail_with=TimeoutError())

    with pytest.raises(CheckError) as raised:
        await run(transport, metric="sysinfo")

    assert "10.0.0.5:161" in str(raised.value)
    # A bare TimeoutError stringifies to nothing at all, and "failed because ''" helps nobody.
    assert "TimeoutError" in str(raised.value)


def test_build_target_defaults_to_v2c_on_the_standard_port_with_the_public_community() -> None:
    target = build_target("10.0.0.5", {}, timeout_seconds=5)

    assert (target.version, target.port, target.community) == ("2c", 161, "public")
    # Not pysnmp's default of five: five retries inside one check turn a 5s timeout into 30.
    assert target.retries == 1


def test_build_target_accepts_v3_with_authentication_and_privacy() -> None:
    target = build_target("10.0.0.5", {
        "version": "3",
        "securityName": "monitor",
        "authProtocol": "SHA-256",
        "authKey": "authpassword",
        "privProtocol": "AES",
        "privKey": "privpassword",
    }, timeout_seconds=5)

    # Spelling is normalised, so "SHA-256", "sha256" and "Sha_256" are one answer.
    assert (target.auth_protocol, target.priv_protocol) == ("sha256", "aes")


@pytest.mark.parametrize(
    ("parameters", "expected"),
    [
        ({"version": "1"}, "must be '2c' or '3'"),
        ({"version": "3"}, "needs a 'securityName'"),
        ({"version": "3", "securityName": "m", "authProtocol": "sha"}, "needs an 'authKey'"),
        (
            {"version": "3", "securityName": "m", "authProtocol": "sha", "authKey": "k",
             "privProtocol": "aes"},
            "needs a 'privKey'",
        ),
        (
            {"version": "3", "securityName": "m", "privProtocol": "aes", "privKey": "k"},
            "privacy requires authentication",
        ),
        ({"port": "70000"}, "at most 65535"),
        ({"authProtocol": "rot13"}, "must be one of"),
    ],
)
def test_build_target_refuses_a_target_snmp_cannot_express(
    parameters: dict[str, str], expected: str,
) -> None:
    with pytest.raises(CheckError, match=expected):
        build_target("10.0.0.5", parameters, timeout_seconds=5)
