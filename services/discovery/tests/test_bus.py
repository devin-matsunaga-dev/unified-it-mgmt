from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

from discovery.bus import DISCOVERED_MESSAGE_URN, build_envelope

FIXTURE = Path(__file__).parent / "fixtures" / "discovered-envelope.json"
PLACEHOLDER = "DISCOVERY_NAME_PLACEHOLDER"


def sample() -> dict[str, Any]:
    return build_envelope(
        json.loads(FIXTURE.read_text())["message"],
        DISCOVERED_MESSAGE_URN,
        source=PLACEHOLDER,
        message_id="0199c0de-4100-7000-8000-00000000beef",
        sent_time=datetime(2026, 8, 13, 12, 0, 0, tzinfo=UTC),
        conversation_id="0199c0de-4100-7000-8000-0000000c0ffe",
    )


def test_envelope_matches_the_fixture_the_dotnet_consumer_is_tested_against() -> None:
    # The other half of this assertion is `DiscoveryEnvelopeTests`, which reads this same file with
    # MassTransit's own serializer options. If this fails, that test is no longer testing what this
    # service publishes — regenerate the fixture and re-run both. Each side testing its own idea of
    # the envelope is exactly how WP-3.2's dead-lettering bug passed a green suite.
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


def test_envelope_names_the_publishing_scanner_in_a_header() -> None:
    assert sample()["headers"]["discovery-source"] == PLACEHOLDER


def test_envelope_message_type_is_the_urn_masstransit_routes_on() -> None:
    assert sample()["messageType"] == ["urn:message:Contracts.Events:DeviceDiscovered"]


def test_fixture_carries_no_community_string() -> None:
    # The fixture is committed, read by two test suites and looked at by anybody debugging the
    # contract. A community in it would be a secret in the repository as well as on the bus.
    assert "community" not in FIXTURE.read_text()
