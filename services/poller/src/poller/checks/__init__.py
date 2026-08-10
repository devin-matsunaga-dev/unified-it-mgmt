"""What a check is, what it produces, and the errors it is allowed to produce it with."""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Any, Protocol


@dataclass(frozen=True, slots=True)
class Metric:
    """
    One measurement. Exactly one of `value` and `text` is populated: a hypertable stores numbers and
    a device record stores names, and WP-3.4 has to tell them apart without a table of metric names.
    """

    name: str
    value: float | None = None
    text: str | None = None
    unit: str | None = None


@dataclass(frozen=True, slots=True)
class CheckOutcome:
    """
    What running one check against one device produced.

    A failed check is still an outcome. A timeout is a fact about the device, and dropping it would
    make an unreachable device indistinguishable from one nobody asked about.
    """

    succeeded: bool
    latency_ms: float | None = None
    error: str | None = None
    metrics: tuple[Metric, ...] = field(default_factory=tuple)

    @classmethod
    def failure(cls, error: str, latency_ms: float | None = None) -> CheckOutcome:
        return cls(succeeded=False, latency_ms=latency_ms, error=error)


class CheckError(Exception):
    """
    A check could not complete, for a reason worth putting in the telemetry.

    Raised rather than returned so that the one place that catches per-target failures catches
    expected and unexpected failures the same way — the rule is that a device never aborts a cycle,
    and a rule with an exception in it is one somebody eventually falls through.
    """


class CheckRunner(Protocol):
    """
    One kind of check. WP-3.8's TCP and HTTP checks implement this beside ICMP and SNMP rather than
    inside them.
    """

    async def run(
        self,
        address: str,
        parameters: Mapping[str, str],
        timeout_seconds: float,
    ) -> CheckOutcome: ...


def parameter_int(
    parameters: Mapping[str, str],
    name: str,
    default: int,
    minimum: int = 1,
    maximum: int | None = None,
) -> int:
    """
    Reads a check parameter written by an operator, and refuses nonsense rather than rounding it.

    Parameters arrive as free-text strings from the API (WP-3.1 stores them as a string dictionary),
    so this is the edge where they stop being text.
    """
    raw = parameters.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        value = int(raw)
    except ValueError as error:
        raise CheckError(f"Parameter '{name}' must be a whole number, got '{raw}'.") from error
    if value < minimum:
        raise CheckError(f"Parameter '{name}' must be at least {minimum}, got {value}.")
    if maximum is not None and value > maximum:
        raise CheckError(f"Parameter '{name}' must be at most {maximum}, got {value}.")
    return value


def describe(error: BaseException) -> str:
    """
    One sentence naming a failure, for the `error` field of a telemetry result.

    Some libraries raise exceptions whose `str` is empty (a bare `TimeoutError` is the common one),
    and "the check failed because ''" helps nobody read their own logs.
    """
    text = str(error).strip()
    return text if text else type(error).__name__


def check_type_of(check: Mapping[str, Any]) -> str:
    """The check's type as the API spelled it; used for dispatch and carried into the telemetry."""
    return str(check.get("type") or "")
