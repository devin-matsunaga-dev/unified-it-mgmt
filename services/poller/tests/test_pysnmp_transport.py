from __future__ import annotations

import asyncio
import threading
import time
from collections.abc import Coroutine, Sequence
from typing import Any

from poller.checks.pysnmp_transport import PySnmpTransport
from poller.checks.snmp import SnmpTarget, SnmpValue, build_target

TARGET: SnmpTarget = build_target("10.0.0.5", {}, timeout_seconds=5)

#: How long the stand-in spends pretending to be pysnmp's BER parser. Well above the 35-81 ms
#: WP-3.8's walk measured, so a loop that were still blocked could not tick during it.
BLOCKING_SECONDS = 0.25


async def count_ticks(during: Coroutine[Any, Any, object]) -> int:
    """Runs `during`, counting how many turns the event loop got while it ran."""
    ticks = 0

    async def tick() -> None:
        nonlocal ticks
        while True:
            await asyncio.sleep(0.005)
            ticks += 1

    ticker = asyncio.create_task(tick())
    try:
        await during
    finally:
        ticker.cancel()
    return ticks


async def test_get_does_its_synchronous_work_off_the_event_loop() -> None:
    """
    WP-3.8's defect, fixed rather than avoided.

    pysnmp does its BER/ASN.1 work synchronously, so a transport that awaited it on the loop stalled
    every other check sharing the cycle — which is what made `check.latency_ms` on a TCP check
    measure how busy the poller was. What is asserted is the property that matters and not the
    library's internals: the blocking work happens on another thread, and the loop keeps running
    while it does.
    """
    transport = PySnmpTransport()
    loop_thread = threading.get_ident()
    ran_on: list[int] = []

    def blocking(target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]:
        ran_on.append(threading.get_ident())
        time.sleep(BLOCKING_SECONDS)
        return {"1.3.6.1.2.1.1.5.0": "sw-hq-01"}

    transport._get = blocking  # type: ignore[method-assign]

    ticks = await count_ticks(transport.get(TARGET, ["1.3.6.1.2.1.1.5.0"]))

    assert ran_on and ran_on[0] != loop_thread
    assert ticks > 5


async def test_walk_does_its_synchronous_work_off_the_event_loop() -> None:
    transport = PySnmpTransport()
    loop_thread = threading.get_ident()
    ran_on: list[int] = []

    def blocking(target: SnmpTarget, root: str) -> dict[str, SnmpValue]:
        ran_on.append(threading.get_ident())
        time.sleep(BLOCKING_SECONDS)
        return {"1": "GigabitEthernet0/1"}

    transport._walk = blocking  # type: ignore[method-assign]

    # The walk is the half this package leans on: an interface poll walks two whole tables, so the
    # parsing it does is a table's worth rather than a handful of scalars.
    ticks = await count_ticks(transport.walk(TARGET, "1.3.6.1.2.1.2.2.1"))

    assert ran_on and ran_on[0] != loop_thread
    assert ticks > 5
