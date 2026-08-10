"""Turning a cycle's results into the two messages the platform consumes."""

from __future__ import annotations

import uuid
from collections.abc import Sequence
from datetime import UTC, datetime
from typing import Any

from .polling import CheckResult, ReachabilityChange

TELEMETRY_MESSAGE_URN = "urn:message:Contracts.Events:DeviceTelemetryReported"
REACHABILITY_MESSAGE_URN = "urn:message:Contracts.Events:DeviceReachabilityChanged"


def build_telemetry(
    results: Sequence[CheckResult],
    poller_name: str,
    poller_group: str,
    cycle_number: int,
    event_id: str | None = None,
    occurred_at: datetime | None = None,
) -> dict[str, Any]:
    """
    One message for a whole cycle's measurements.

    Batched because a poller with two hundred devices and four checks each would otherwise publish
    eight hundred messages a cycle to say the same thing, and because the results of one cycle are
    one observation of the estate — a consumer that receives half of them has half an answer either
    way, and this way it knows.
    """
    return {
        "eventId": event_id or str(uuid.uuid4()),
        "occurredAt": (occurred_at or datetime.now(UTC)).isoformat(),
        "pollerName": poller_name,
        "pollerGroup": poller_group,
        "cycleNumber": cycle_number,
        "results": [_result(result) for result in results],
    }


def build_reachability(
    change: ReachabilityChange,
    poller_name: str,
    poller_group: str,
    event_id: str | None = None,
    occurred_at: datetime | None = None,
) -> dict[str, Any]:
    """One message per transition — not per cycle, and not per failed check."""
    return {
        "eventId": event_id or str(uuid.uuid4()),
        "occurredAt": (occurred_at or datetime.now(UTC)).isoformat(),
        "deviceId": change.device_id,
        "ciId": change.ci_id,
        "address": change.address,
        "pollerName": poller_name,
        "pollerGroup": poller_group,
        "isReachable": change.is_reachable,
        "consecutiveFailures": change.consecutive_failures,
        "error": change.error,
    }


def _result(result: CheckResult) -> dict[str, Any]:
    return {
        "deviceId": result.due.device_id,
        "ciId": result.due.ci_id,
        "checkId": result.due.check_id,
        "checkType": result.due.check_type,
        "checkName": result.due.check_name,
        "address": result.due.address,
        "observedAt": result.observed_at.isoformat(),
        "succeeded": result.outcome.succeeded,
        "latencyMs": result.outcome.latency_ms,
        "error": result.outcome.error,
        "metrics": [
            {
                "name": metric.name,
                "value": metric.value,
                "text": metric.text,
                "unit": metric.unit,
            }
            for metric in result.outcome.metrics
        ],
    }
