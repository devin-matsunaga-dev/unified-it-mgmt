from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

from poller.bus import HEARTBEAT_MESSAGE_URN, build_envelope

FIXTURE = Path(__file__).parent / "fixtures" / "heartbeat-envelope.json"
PLACEHOLDER = "POLLER_NAME_PLACEHOLDER"


def sample() -> dict[str, Any]:
    return build_envelope(
        json.loads(FIXTURE.read_text())["message"],
        HEARTBEAT_MESSAGE_URN,
        source=PLACEHOLDER,
        message_id="0199c0de-0000-7000-8000-00000000beef",
        sent_time=datetime(2026, 8, 10, 12, 0, 0, tzinfo=UTC),
        conversation_id="0199c0de-0000-7000-8000-0000000c0ffe",
    )


def test_envelope_matches_the_fixture_the_dotnet_consumer_is_tested_against() -> None:
    # The fixture is fed to the real MassTransit consumer by
    # PollerEnvelopeIntegrationTests. If this assertion fails, that test is no longer testing what
    # this service actually publishes — regenerate the fixture and re-run both.
    assert sample() == json.loads(FIXTURE.read_text())


def test_envelope_carries_no_address_fields() -> None:
    envelope = sample()

    # MassTransit parses these as absolute URIs while deserialising, before any consumer runs. The
    # exchange name contains a colon, so an address built from it reads as a host and a port and
    # dead-letters every message with "Invalid port specified".
    assert "sourceAddress" not in envelope
    assert "destinationAddress" not in envelope


def test_envelope_any_future_address_field_must_be_an_absolute_uri() -> None:
    envelope = sample()

    for key in ("sourceAddress", "destinationAddress", "responseAddress", "faultAddress"):
        if key in envelope:
            parsed = urlparse(str(envelope[key]))
            assert parsed.scheme and parsed.netloc, f"{key} is not an absolute URI"


def test_envelope_names_the_publishing_poller_in_a_header() -> None:
    assert sample()["headers"]["poller-source"] == PLACEHOLDER


def test_envelope_message_type_is_the_urn_masstransit_routes_on() -> None:
    assert sample()["messageType"] == ["urn:message:Contracts.Events:PollerHeartbeat"]
