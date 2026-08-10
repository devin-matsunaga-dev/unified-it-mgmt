"""Entry point: wire the real HTTP client and broker connection into the cycle, then run it."""

from __future__ import annotations

import asyncio
import logging
import signal

import aio_pika
import httpx

from .agent import PollerAgent
from .api import PlatformApiClient
from .bus import ExchangePublisher
from .checks.icmp import IcmpCheck
from .checks.snmp import SnmpCheck
from .logging import configure_logging
from .polling import PollingEngine
from .settings import Settings

logger = logging.getLogger("poller")


async def main() -> None:
    configure_logging()
    settings = Settings.from_env()
    logger.info(
        "Starting.",
        extra={
            "poller": settings.name,
            "poller_group": settings.poller_group,
            "agent_version": settings.agent_version,
            "interval_seconds": settings.interval_seconds,
            "api_base_url": settings.api_base_url,
        },
    )

    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for received in (signal.SIGINT, signal.SIGTERM):
        # `docker stop` sends SIGTERM, and the poller finishing its cycle and closing its
        # connection is the difference between a clean stop and a broker-side timeout.
        loop.add_signal_handler(received, stop.set)

    timeout = httpx.Timeout(settings.http_timeout_seconds)
    async with httpx.AsyncClient(timeout=timeout) as http:
        connection = await aio_pika.connect_robust(settings.amqp_url)
        async with connection:
            agent = PollerAgent(
                settings,
                PlatformApiClient(settings, http),
                ExchangePublisher(connection, settings.heartbeat_exchange),
                # Keyed by the check type as the API spells it, matched case-insensitively. A type
                # with no runner here is reported as one this poller cannot run, rather than
                # dropped: TCP and HTTP arrive in WP-3.8.
                engine=PollingEngine(
                    {
                        "Icmp": IcmpCheck(privileged=settings.icmp_privileged),
                        "Snmp": SnmpCheck(),
                    },
                    max_concurrency=settings.max_concurrent_checks,
                ),
                telemetry_publisher=ExchangePublisher(connection, settings.telemetry_exchange),
                reachability_publisher=ExchangePublisher(
                    connection, settings.reachability_exchange),
            )
            await agent.run_forever(stop)

    logger.info("Stopped.", extra={"poller": settings.name})


def run() -> None:
    asyncio.run(main())


if __name__ == "__main__":
    run()
