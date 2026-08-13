"""Entry point: wire the real HTTP client and broker connection into the cycle, then run it."""

from __future__ import annotations

import asyncio
import logging
import signal

import aio_pika
import httpx

from .agent import DiscoveryAgent
from .api import PlatformApiClient
from .bus import ExchangePublisher
from .logging import configure_logging
from .scanner import Scanner
from .settings import Settings
from .sweep import NetworkSweep

logger = logging.getLogger("discovery")


async def main() -> None:
    configure_logging()
    settings = Settings.from_env()
    logger.info(
        "Starting.",
        extra={
            "discovery": settings.name,
            "discovery_group": settings.discovery_group,
            "agent_version": settings.agent_version,
            "interval_seconds": settings.interval_seconds,
            "api_base_url": settings.api_base_url,
            # A count, never the values. The communities are the only secrets this process holds.
            "snmp_communities": len(settings.snmp_communities),
        },
    )

    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for received in (signal.SIGINT, signal.SIGTERM):
        # `docker stop` sends SIGTERM, and finishing the cycle rather than dropping hundreds of
        # in-flight sockets is the difference between a clean stop and a broker-side timeout.
        loop.add_signal_handler(received, stop.set)

    timeout = httpx.Timeout(settings.http_timeout_seconds)
    async with httpx.AsyncClient(timeout=timeout) as http:
        connection = await aio_pika.connect_robust(settings.amqp_url)
        async with connection:
            agent = DiscoveryAgent(
                settings,
                PlatformApiClient(settings, http),
                Scanner(
                    NetworkSweep(
                        privileged=settings.icmp_privileged,
                        max_concurrency=settings.max_concurrent_probes,
                    ),
                    communities=settings.snmp_communities,
                ),
                publisher=ExchangePublisher(connection, settings.discovered_exchange),
            )
            await agent.run_forever(stop)

    logger.info("Stopped.", extra={"discovery": settings.name})


def run() -> None:
    asyncio.run(main())


if __name__ == "__main__":
    run()
