"""
IF-MIB interface polling: what a link is, how fast it is running, and what it is dropping.

Everything here is pure except :class:`InterfaceRateCache`, which remembers one previous reading per
interface. That memory is the whole difficulty of interface monitoring: the MIB counts octets since
the agent booted, and "12 Mbit/s" is a subtraction between two of those counts over the seconds
between them. A poller that has only just started, or one whose device rebooted, has nothing to
subtract from and reports the link's state without a rate rather than reporting a rate of zero — a
zero would draw a flat line on a chart and read as a quiet link rather than an unmeasured one.
"""

from __future__ import annotations

import logging
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass, field

from . import Metric
from . import oids as oid

logger = logging.getLogger("poller.checks.interfaces")

#: How many interfaces one check reports. A 48-port switch is 48; the cap exists for the device that
#: answers with a row per VLAN, per sub-interface and per tunnel, which is thousands — and which
#: would otherwise publish a telemetry batch nobody can read and a chart picker nobody can use.
MAX_INTERFACES = 256

#: How stale a remembered counter may be before it is a new baseline instead of a subtrahend. A
#: poller that missed a quarter of an hour can still divide, but the answer is an average over a gap
#: it cannot see into, reported as if it were a reading taken now.
MAX_BASELINE_AGE_SECONDS = 900.0

#: The counters a rate is derived from, and the metric each rate is published as. The `octets` pair
#: is multiplied out to bits; the rest are counts of events per second.
RATE_METRICS: Mapping[str, str] = {
    "octets_in": "bits_in_per_second",
    "octets_out": "bits_out_per_second",
    "errors_in": "errors_in_per_second",
    "errors_out": "errors_out_per_second",
    "discards_in": "discards_in_per_second",
    "discards_out": "discards_out_per_second",
}

#: Metric names are `interface.<ifIndex>.<field>`, and the module that parses them back apart is
#: `InterfaceMetricNames` in Modules.Monitoring. The two mirror each other by hand — the same
#: standing hazard `AlertRules.PrimaryMetric` already carries for every other check type.
METRIC_PREFIX = "interface"


@dataclass(frozen=True, slots=True)
class InterfaceReading:
    """One interface, as the two tables describe it. Counters are raw totals, not rates."""

    index: int
    name: str | None = None
    alias: str | None = None
    type: int | None = None
    admin_status: int | None = None
    oper_status: int | None = None
    speed_bits_per_second: float | None = None
    physical_address: str | None = None
    counters: Mapping[str, float] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class _Sample:
    at: float
    counters: Mapping[str, float]


class InterfaceRateCache:
    """
    The previous cycle's counters, per agent and interface.

    Held on the check runner rather than on the device configuration, because it is a property of
    this process having polled before — a restarted poller has genuinely not measured anything yet,
    and pretending otherwise by persisting it would produce one enormous rate on the first cycle
    covering however long the poller was down.
    """

    def __init__(self, max_baseline_age_seconds: float = MAX_BASELINE_AGE_SECONDS) -> None:
        self._samples: dict[tuple[str, int], _Sample] = {}
        self._max_age = max_baseline_age_seconds

    def sample(
        self,
        agent: str,
        reading: InterfaceReading,
        at: float,
    ) -> dict[str, float]:
        """
        Records this cycle's counters and returns the per-second rates they imply.

        Empty on the first sighting of an interface, on a counter that went backwards (an agent
        restart or a 32-bit counter that wrapped — indistinguishable, and neither difference is
        traffic), and on a baseline too old to subtract from honestly.
        """
        key = (agent, reading.index)
        previous = self._samples.get(key)
        self._samples[key] = _Sample(at, dict(reading.counters))

        if previous is None:
            return {}

        elapsed = at - previous.at
        if elapsed <= 0 or elapsed > self._max_age:
            return {}

        rates: dict[str, float] = {}
        for name, value in reading.counters.items():
            before = previous.counters.get(name)
            if before is None or value < before:
                continue
            rates[name] = (value - before) / elapsed
        return rates

    def prune(self, agent: str, seen: Iterable[int]) -> None:
        """
        Forgets interfaces this agent no longer reports.

        `PollingEngine.retain`'s rule, applied one level down: a process that runs for months must
        not accumulate a row per interface a device once had, and an interface that comes back after
        being removed from the table must be measured from a fresh baseline rather than against
        whatever it read the last time anybody saw it.
        """
        keep = set(seen)
        for key in [key for key in self._samples if key[0] == agent and key[1] not in keep]:
            del self._samples[key]

    def expire(self, at: float) -> None:
        """
        Drops every baseline too old to be subtracted from.

        This is what bounds the cache over months of running, and it is time-based rather than wired
        to the configuration on purpose: a device moved to another poller group, or one whose config
        the server has disowned, simply stops being sampled — and an entry nobody has updated for
        longer than the baseline window is one no cycle could have used anyway.
        """
        for key in [key for key, sample in self._samples.items() if at - sample.at > self._max_age]:
            del self._samples[key]


def read_table(
    if_table: Mapping[str, object],
    if_x_table: Mapping[str, object],
) -> list[InterfaceReading]:
    """
    Turns two subtree walks into one reading per interface.

    A walk of `1.3.6.1.2.1.2.2.1` comes back keyed `<column>.<index>`, so this is where a column
    number stops being an OID fragment. Rows are matched by index across the two tables: ifXTable is
    optional (a device that has never heard of it is common on older access switches) and every
    field it carries has an ifTable fallback.
    """
    by_index: dict[int, dict[int, object]] = {}
    for column, index, value in _columns(if_table):
        by_index.setdefault(index, {})[column] = value

    extended: dict[int, dict[int, object]] = {}
    for column, index, value in _columns(if_x_table):
        extended.setdefault(index, {})[column] = value

    readings: list[InterfaceReading] = []
    for index in sorted(by_index.keys() | extended.keys()):
        base = by_index.get(index, {})
        more = extended.get(index, {})

        # ifName is the short form a switch prints on its own console ("Gi0/1"); ifDescr is the
        # long one. Preferring ifName makes the table read the way the device's own CLI does, and
        # falling back means a device answering only the old table still has named interfaces.
        name = _text(more.get(oid.IF_NAME)) or _text(base.get(oid.IF_DESCR))

        counters: dict[str, float] = {}
        _put(counters, "octets_in", more.get(oid.IF_HC_IN_OCTETS), base.get(oid.IF_IN_OCTETS))
        _put(counters, "octets_out", more.get(oid.IF_HC_OUT_OCTETS), base.get(oid.IF_OUT_OCTETS))
        _put(counters, "errors_in", base.get(oid.IF_IN_ERRORS))
        _put(counters, "errors_out", base.get(oid.IF_OUT_ERRORS))
        _put(counters, "discards_in", base.get(oid.IF_IN_DISCARDS))
        _put(counters, "discards_out", base.get(oid.IF_OUT_DISCARDS))

        readings.append(InterfaceReading(
            index=index,
            name=name,
            alias=_text(more.get(oid.IF_ALIAS)),
            type=_integer(base.get(oid.IF_TYPE)),
            admin_status=_integer(base.get(oid.IF_ADMIN_STATUS)),
            oper_status=_integer(base.get(oid.IF_OPER_STATUS)),
            speed_bits_per_second=_speed(more.get(oid.IF_HIGH_SPEED), base.get(oid.IF_SPEED)),
            physical_address=_text(base.get(oid.IF_PHYS_ADDRESS)),
            counters=counters,
        ))

    if len(readings) > MAX_INTERFACES:
        logger.warning(
            "An agent reported more interfaces than one check publishes.",
            extra={"reported": len(readings), "published": MAX_INTERFACES},
        )
        return readings[:MAX_INTERFACES]
    return readings


def measure(
    readings: Sequence[InterfaceReading],
    cache: InterfaceRateCache,
    agent: str,
    at: float,
) -> list[Metric]:
    """
    Everything one interface poll publishes: what each link is, and what it is carrying.

    The identity fields travel every cycle rather than once, because they are how the platform
    learns an interface exists at all — there is no separate registration, and a port renamed on the
    switch should be renamed on the next poll rather than on whatever event nobody sends.
    """
    cache.expire(at)
    cache.prune(agent, (reading.index for reading in readings))

    metrics: list[Metric] = []
    for reading in readings:
        prefix = f"{METRIC_PREFIX}.{reading.index}"

        if reading.name:
            metrics.append(Metric(f"{prefix}.name", text=reading.name))
        if reading.alias:
            metrics.append(Metric(f"{prefix}.alias", text=reading.alias))
        if reading.physical_address:
            metrics.append(Metric(f"{prefix}.mac_address", text=reading.physical_address))
        if reading.type is not None:
            metrics.append(Metric(f"{prefix}.type", value=float(reading.type)))
        if reading.admin_status is not None:
            metrics.append(Metric(f"{prefix}.admin_status", value=float(reading.admin_status)))
        if reading.oper_status is not None:
            # The IF-MIB number (1 up, 2 down, 3 testing, …) rather than the word: a hypertable
            # stores numbers and an alert rule compares one. The words live in the browser, which is
            # the only place anybody reads them.
            metrics.append(Metric(f"{prefix}.oper_status", value=float(reading.oper_status)))
        if reading.speed_bits_per_second is not None:
            metrics.append(Metric(
                f"{prefix}.speed_bits_per_second",
                value=reading.speed_bits_per_second,
                unit="bit/s",
            ))

        rates = cache.sample(agent, reading, at)
        bits: dict[str, float] = {}
        for counter, rate in sorted(rates.items()):
            name = RATE_METRICS.get(counter)
            if name is None:
                continue
            if counter.startswith("octets_"):
                rate *= oid.BITS_PER_OCTET
                bits[counter] = rate
            metrics.append(Metric(
                f"{prefix}.{name}",
                value=rate,
                unit="bit/s" if counter.startswith("octets_") else "1/s",
            ))

        utilisation = _utilisation(bits, reading.speed_bits_per_second)
        if utilisation is not None:
            metrics.append(Metric(f"{prefix}.utilisation_percent", value=utilisation, unit="%"))

    return metrics


def _utilisation(bits: Mapping[str, float], speed: float | None) -> float | None:
    """
    The busier direction as a percentage of the link's speed.

    Not the sum of the two: a full-duplex link carries its rated speed in each direction at once, so
    a saturated download and a saturated upload is 100% twice over rather than 200% of anything. The
    busier direction is what an operator means by "that port is full", and it is the number a
    threshold is worth setting on.

    Deliberately not clamped to 100. A figure above it means the speed the agent reports is not the
    speed of the link — a hardcoded ifSpeed on a virtual interface, most often — and clamping would
    turn a configuration fault that reads as 900% into a port that appears permanently saturated.
    """
    if speed is None or speed <= 0 or not bits:
        return None
    return max(bits.values()) / speed * 100


def _columns(table: Mapping[str, object]) -> list[tuple[int, int, object]]:
    """
    Splits `<column>.<index>` keys, dropping anything that is not one.

    IF-MIB indexes an interface with a single sub-identifier, so a key with anything else after the
    column — a row from a table the agent answered with although it was not asked, which WP-3.3's
    walk already guards the other end of — is not an interface and is left out rather than turned
    into a column of nonsense on a row of real measurements.
    """
    parsed: list[tuple[int, int, object]] = []
    for key, value in table.items():
        column, separator, index = key.partition(".")
        if not separator or not column.isdigit() or not index.isdigit():
            continue
        parsed.append((int(column), int(index), value))
    return parsed


def _put(counters: dict[str, float], name: str, *candidates: object) -> None:
    """Takes the first candidate that is a number, so a 64-bit counter beats its 32-bit twin."""
    for candidate in candidates:
        if (value := _number(candidate)) is not None:
            counters[name] = value
            return


def _speed(high_speed: object, speed: object) -> float | None:
    """
    The link's speed in bits per second, preferring ifHighSpeed.

    ifSpeed is a Gauge32 in bits per second, so it saturates at 4.29 Gbit/s and reports a 10 Gbit/s
    port as 4.29 — which would make a busy uplink read as over 200% utilised. ifHighSpeed is
    megabits and has no such ceiling. A port that is administratively down reports zero on both,
    which is not a speed and is why zero is treated as absent.
    """
    if (megabits := _number(high_speed)) is not None and megabits > 0:
        return megabits * oid.BITS_PER_MEGABIT
    if (bits := _number(speed)) is not None and bits > 0:
        return bits
    return None


def _number(value: object) -> float | None:
    if value is None:
        return None
    try:
        return float(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return None


def _integer(value: object) -> int | None:
    number = _number(value)
    return None if number is None else int(number)


def _text(value: object) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text or None
