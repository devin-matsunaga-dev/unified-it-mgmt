"""Publishing to RabbitMQ, which is the only thing this credential is allowed to do."""

from __future__ import annotations

import json
import uuid
from datetime import UTC, datetime
from typing import Any, Protocol

import aio_pika

CONTENT_TYPE = "application/vnd.masstransit+json"
HEARTBEAT_MESSAGE_URN = "urn:message:Contracts.Events:PollerHeartbeat"


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

    The consumer on the other side is a .NET `IConsumer<PollerHeartbeat>`, and MassTransit routes by
    the URN in `messageType` rather than by the exchange alone. Getting this shape wrong produces a
    message that arrives, is not recognised, and is quietly skipped — so it is built in one place
    and asserted in the tests.

    `sourceAddress` and `destinationAddress` are deliberately absent. MassTransit parses both as
    absolute URIs while deserialising, before any consumer sees the message, and the exchange name
    contains a colon — so `exchange://Contracts.Events:PollerHeartbeat` is read as a host and a port
    and fails with "Invalid port specified", dead-lettering every beat. They are optional and carry
    nothing this consumer needs; the poller identifies itself in a header instead.
    """
    moment = sent_time or datetime.now(UTC)
    return {
        "messageId": message_id or str(uuid.uuid4()),
        "conversationId": conversation_id or str(uuid.uuid4()),
        "messageType": [message_urn],
        "message": message,
        "sentTime": moment.isoformat(),
        "headers": {"poller-source": source},
    }


class Publisher(Protocol):
    """The one bus operation this service performs, so tests can stand in for the broker."""

    async def publish(self, envelope: dict[str, Any]) -> None: ...


class HeartbeatPublisher:
    """
    Publishes to the heartbeat exchange and nothing else.

    It deliberately never declares that exchange: the poller's account has no `configure`
    permission, and the broker's definitions file declares it at boot. A declare here would fail
    with ACCESS_REFUSED and take the poller down — which is the permission model working, but at
    the wrong moment.
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
