"""Publishing to RabbitMQ, which is the only thing this credential is allowed to do."""

from __future__ import annotations

import json
import uuid
from datetime import UTC, datetime
from typing import Any, Protocol

import aio_pika

CONTENT_TYPE = "application/vnd.masstransit+json"
DISCOVERED_MESSAGE_URN = "urn:message:Contracts.Events:DeviceDiscovered"


class NullPublisher:
    """Accepts and drops. Stands in where a publisher is optional, so no caller tests for None."""

    async def publish(self, envelope: dict[str, Any]) -> None:
        return None


def build_envelope(
    message: dict[str, Any],
    message_urn: str,
    source: str,
    message_id: str | None = None,
    sent_time: datetime | None = None,
    conversation_id: str | None = None,
) -> dict[str, Any]:
    """
    Wraps a payload in the envelope MassTransit expects.

    Deliberately identical in shape to the poller's, including what it leaves out. `sourceAddress`
    and `destinationAddress` are absent because MassTransit parses both as absolute URIs while
    deserialising, before any consumer runs, and an exchange name contains a colon — so
    `exchange://Contracts.Events:DeviceDiscovered` reads as a host and a port and dead-letters
    every message. That cost WP-3.2 a live debugging session; this service starts from the answer.

    The two services do not share this code, because they are separate deployables with separate
    dependency sets. `tests/fixtures/discovered-envelope.json` plus `DiscoveryEnvelopeTests` on the
    .NET side is what stops the two copies from drifting, which is the same guard WP-3.2 built.
    """
    moment = sent_time or datetime.now(UTC)
    return {
        "messageId": message_id or str(uuid.uuid4()),
        "conversationId": conversation_id or str(uuid.uuid4()),
        "messageType": [message_urn],
        "message": message,
        "sentTime": moment.isoformat(),
        "headers": {"discovery-source": source},
    }


class Publisher(Protocol):
    """The one bus operation this service performs, so tests can stand in for the broker."""

    async def publish(self, envelope: dict[str, Any]) -> None: ...


class ExchangePublisher:
    """
    Publishes to one exchange and nothing else.

    It never declares that exchange: this service's broker account has no `configure` permission,
    and the definitions file declares it at boot. A declare here would fail with ACCESS_REFUSED and
    take the scanner down — the permission model working, at the wrong moment.
    """

    def __init__(
        self,
        connection: aio_pika.abc.AbstractRobustConnection,
        exchange_name: str,
    ) -> None:
        self._connection = connection
        self._exchange_name = exchange_name
        self._exchange: aio_pika.abc.AbstractExchange | None = None

    async def publish(self, envelope: dict[str, Any]) -> None:
        exchange = await self._ensure_exchange()
        await exchange.publish(
            aio_pika.Message(
                body=json.dumps(envelope).encode("utf-8"),
                content_type=CONTENT_TYPE,
                message_id=str(envelope["messageId"]),
                delivery_mode=aio_pika.DeliveryMode.PERSISTENT,
            ),
            routing_key="",
        )

    async def _ensure_exchange(self) -> aio_pika.abc.AbstractExchange:
        if self._exchange is None:
            channel = await self._connection.channel()
            # ensure=False: look the exchange up without asking the broker to declare it.
            self._exchange = await channel.get_exchange(self._exchange_name, ensure=False)
        return self._exchange
