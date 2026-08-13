from __future__ import annotations

from collections.abc import Mapping

from poller.checks import Metric
from poller.checks import oids as oid
from poller.checks.interfaces import (
    MAX_INTERFACES,
    InterfaceRateCache,
    InterfaceReading,
    measure,
    read_table,
)

#: One gigabit port, up, with both counter families answered. Written as OIDs-minus-the-root, which
#: is exactly what `SnmpTransport.walk` hands back.
IF_TABLE: Mapping[str, str] = {
    f"{oid.IF_DESCR}.1": "GigabitEthernet0/1",
    f"{oid.IF_TYPE}.1": "6",
    f"{oid.IF_SPEED}.1": "1000000000",
    f"{oid.IF_PHYS_ADDRESS}.1": "00:1b:0d:aa:bb:01",
    f"{oid.IF_ADMIN_STATUS}.1": "1",
    f"{oid.IF_OPER_STATUS}.1": "1",
    f"{oid.IF_IN_OCTETS}.1": "1000",
    f"{oid.IF_OUT_OCTETS}.1": "2000",
    f"{oid.IF_IN_ERRORS}.1": "0",
    f"{oid.IF_OUT_ERRORS}.1": "0",
    f"{oid.IF_IN_DISCARDS}.1": "0",
    f"{oid.IF_OUT_DISCARDS}.1": "0",
}

IF_X_TABLE: Mapping[str, str] = {
    f"{oid.IF_NAME}.1": "Gi0/1",
    f"{oid.IF_ALIAS}.1": "uplink to core",
    f"{oid.IF_HC_IN_OCTETS}.1": "1000",
    f"{oid.IF_HC_OUT_OCTETS}.1": "2000",
    f"{oid.IF_HIGH_SPEED}.1": "1000",
}


def values_of(metrics: list[Metric]) -> dict[str, float | str | None]:
    return {metric.name: metric.value if metric.value is not None else metric.text
            for metric in metrics}


def counters(**named: float) -> dict[str, float]:
    return dict(named)


# --- reading the tables --------------------------------------------------------------------------

def test_read_table_merges_the_two_tables_into_one_reading_per_interface() -> None:
    [interface] = read_table(IF_TABLE, IF_X_TABLE)

    assert interface.index == 1
    # ifName, not ifDescr: the short form is what the switch's own console prints.
    assert interface.name == "Gi0/1"
    assert interface.alias == "uplink to core"
    assert interface.type == 6
    assert interface.admin_status == 1
    assert interface.oper_status == 1
    assert interface.physical_address == "00:1b:0d:aa:bb:01"
    assert interface.speed_bits_per_second == 1_000_000_000


def test_read_table_falls_back_to_the_base_table_when_there_is_no_ifxtable() -> None:
    [interface] = read_table(IF_TABLE, {})

    # An access switch old enough to have no ifXTable still has named interfaces, a speed and a
    # rate — it simply loses the alias and the 64-bit counters.
    assert interface.name == "GigabitEthernet0/1"
    assert interface.alias is None
    assert interface.speed_bits_per_second == 1_000_000_000
    assert interface.counters["octets_in"] == 1000


def test_read_table_prefers_the_64_bit_counters_where_the_agent_answers_both() -> None:
    [interface] = read_table(
        {**IF_TABLE, f"{oid.IF_IN_OCTETS}.1": "17"},
        {**IF_X_TABLE, f"{oid.IF_HC_IN_OCTETS}.1": "4294968000"},
    )

    # The 32-bit counter has already wrapped; the 64-bit one carries the real total, and a poller
    # that took the smaller number would report the wrap as a reset every 34 seconds on a busy link.
    assert interface.counters["octets_in"] == 4_294_968_000


def test_read_table_prefers_ifhighspeed_because_ifspeed_saturates_at_four_gigabits() -> None:
    [interface] = read_table(
        {**IF_TABLE, f"{oid.IF_SPEED}.1": "4294967295"},
        {**IF_X_TABLE, f"{oid.IF_HIGH_SPEED}.1": "10000"},
    )

    assert interface.speed_bits_per_second == 10_000_000_000


def test_read_table_treats_a_zero_speed_as_no_speed_rather_than_a_link_of_no_capacity() -> None:
    [interface] = read_table(
        {**IF_TABLE, f"{oid.IF_SPEED}.1": "0"},
        {**IF_X_TABLE, f"{oid.IF_HIGH_SPEED}.1": "0"},
    )

    assert interface.speed_bits_per_second is None


def test_read_table_ignores_rows_that_are_not_an_interface() -> None:
    interfaces = read_table(
        {
            **IF_TABLE,
            # A sub-identifier too many: something the agent answered from outside the table.
            f"{oid.IF_OPER_STATUS}.1.4": "2",
            "not-a-column.1": "x",
        },
        IF_X_TABLE,
    )

    assert [interface.index for interface in interfaces] == [1]


def test_read_table_publishes_at_most_the_interface_cap() -> None:
    crowded = {
        f"{column}.{index}": "1"
        for index in range(1, MAX_INTERFACES + 20)
        for column in (oid.IF_DESCR, oid.IF_OPER_STATUS)
    }

    interfaces = read_table(crowded, {})

    assert len(interfaces) == MAX_INTERFACES
    # Lowest indices, so the physical ports survive and the thousandth VLAN is what is dropped.
    assert interfaces[0].index == 1
    assert interfaces[-1].index == MAX_INTERFACES


# --- rates ---------------------------------------------------------------------------------------

def test_measure_reports_the_link_without_a_rate_on_the_first_cycle() -> None:
    readings = read_table(IF_TABLE, IF_X_TABLE)
    metrics = values_of(measure(readings, InterfaceRateCache(), "a:161", 10))

    assert metrics["interface.1.name"] == "Gi0/1"
    assert metrics["interface.1.oper_status"] == 1
    assert metrics["interface.1.speed_bits_per_second"] == 1_000_000_000
    # Nothing to subtract from. A zero here would draw a flat line on a chart and read as a quiet
    # link rather than one nobody has measured twice yet.
    assert "interface.1.bits_in_per_second" not in metrics
    assert "interface.1.utilisation_percent" not in metrics


def test_measure_turns_two_octet_counts_into_bits_per_second_and_a_utilisation() -> None:
    cache = InterfaceRateCache()
    measure(read_table(IF_TABLE, IF_X_TABLE), cache, "a:161", 10)

    later = read_table(
        IF_TABLE,
        {**IF_X_TABLE,
         f"{oid.IF_HC_IN_OCTETS}.1": "12_501_000", f"{oid.IF_HC_OUT_OCTETS}.1": "3000"},
    )
    metrics = values_of(measure(later, cache, "a:161", 20))

    # 12,500,000 octets in ten seconds is 1,250,000 octets/s, which is 10 Mbit/s of a gigabit link.
    assert metrics["interface.1.bits_in_per_second"] == 10_000_000
    assert metrics["interface.1.bits_out_per_second"] == 800
    assert metrics["interface.1.utilisation_percent"] == 1.0


def test_measure_reads_utilisation_off_the_busier_direction_rather_than_the_sum() -> None:
    cache = InterfaceRateCache()
    measure(read_table(IF_TABLE, IF_X_TABLE), cache, "a:161", 0)

    saturated = read_table(IF_TABLE, {
        **IF_X_TABLE,
        f"{oid.IF_HC_IN_OCTETS}.1": str(1000 + 125_000_000),
        f"{oid.IF_HC_OUT_OCTETS}.1": str(2000 + 125_000_000),
    })
    metrics = values_of(measure(saturated, cache, "a:161", 1))

    # Both directions full on a full-duplex gigabit link is 100%, not 200%: the link carries its
    # rated speed each way at once.
    assert metrics["interface.1.utilisation_percent"] == 100.0


def test_measure_does_not_clamp_a_utilisation_above_a_speed_the_agent_reports_wrongly() -> None:
    cache = InterfaceRateCache()
    slow = {**IF_X_TABLE, f"{oid.IF_HIGH_SPEED}.1": "1"}
    measure(read_table(IF_TABLE, slow), cache, "a:161", 0)

    busy = read_table(IF_TABLE, {**slow, f"{oid.IF_HC_IN_OCTETS}.1": str(1000 + 1_250_000)})
    metrics = values_of(measure(busy, cache, "a:161", 1))

    # 10 Mbit/s down a link the agent says is 1 Mbit/s. Clamping it to 100% would turn a wrong
    # ifHighSpeed into a port that merely looks permanently saturated.
    assert metrics["interface.1.utilisation_percent"] == 1000.0


def test_measure_reports_no_rate_when_a_counter_went_backwards() -> None:
    cache = InterfaceRateCache()
    measure(read_table(IF_TABLE, IF_X_TABLE), cache, "a:161", 10)

    rebooted = read_table(IF_TABLE, {**IF_X_TABLE, f"{oid.IF_HC_IN_OCTETS}.1": "5"})
    metrics = values_of(measure(rebooted, cache, "a:161", 20))

    # An agent restart and a wrapped counter look identical from here, and neither difference is
    # traffic. The out direction still rose, so it is still reported.
    assert "interface.1.bits_in_per_second" not in metrics
    assert metrics["interface.1.bits_out_per_second"] == 0


def test_measure_reports_no_rate_across_a_baseline_too_old_to_subtract_from() -> None:
    cache = InterfaceRateCache(max_baseline_age_seconds=60)
    measure(read_table(IF_TABLE, IF_X_TABLE), cache, "a:161", 0)

    later = read_table(IF_TABLE, {**IF_X_TABLE, f"{oid.IF_HC_IN_OCTETS}.1": "999999999"})
    metrics = values_of(measure(later, cache, "a:161", 3600))

    # An hour of traffic averaged into one sample and stamped as now would read as a spike that
    # happened this minute.
    assert "interface.1.bits_in_per_second" not in metrics
    # …and this cycle is the new baseline, so the next one measures normally.
    assert "interface.1.oper_status" in metrics


def test_measure_never_subtracts_one_agents_counters_from_anothers() -> None:
    cache = InterfaceRateCache()
    measure(read_table(IF_TABLE, IF_X_TABLE), cache, "a:161", 10)

    other = values_of(measure(read_table(IF_TABLE, IF_X_TABLE), cache, "b:161", 20))

    assert "interface.1.bits_in_per_second" not in other


def test_measure_forgets_an_interface_the_device_stopped_reporting() -> None:
    cache = InterfaceRateCache()
    two_ports = read_table(
        {**IF_TABLE, f"{oid.IF_DESCR}.2": "Gi0/2", f"{oid.IF_IN_OCTETS}.2": "50"}, IF_X_TABLE)
    measure(two_ports, cache, "a:161", 10)

    # The module was pulled out of the stack; the port comes back later as a fresh baseline rather
    # than as however many octets it had counted when it left.
    measure(read_table(IF_TABLE, IF_X_TABLE), cache, "a:161", 20)
    returned = values_of(measure(two_ports, cache, "a:161", 30))

    assert "interface.2.bits_in_per_second" not in returned


def test_rate_cache_sample_returns_nothing_for_an_interface_it_has_not_seen() -> None:
    cache = InterfaceRateCache()

    assert cache.sample("a:161", InterfaceReading(1, counters=counters(octets_in=10)), 1) == {}
    assert cache.sample("a:161", InterfaceReading(1, counters=counters(octets_in=20)), 2) == {
        "octets_in": 10,
    }


def test_rate_cache_expires_a_baseline_nothing_has_updated() -> None:
    """
    What bounds the cache in a process that runs for months: a device moved to another poller group
    stops being sampled, and its ports age out rather than being remembered forever.
    """
    cache = InterfaceRateCache(max_baseline_age_seconds=60)
    cache.sample("a:161", InterfaceReading(1, counters=counters(octets_in=10)), 1)

    cache.expire(500)

    assert cache.sample("a:161", InterfaceReading(1, counters=counters(octets_in=20)), 501) == {}
