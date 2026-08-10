from __future__ import annotations

import json
from typing import Any

import pytest

from poller.agent import PollerAgent
from poller.api import ConfigVersionRejectedError, PollerNotRegisteredError
from poller.bus import HEARTBEAT_MESSAGE_URN
from poller.settings import Settings
from tests.test_config import delta, snapshot

SETTINGS = Settings(
    name="poller-1",
    poller_group="default",
    agent_version="0.1.0",
    interval_seconds=15,
    api_base_url="http://localhost:5000",
    oidc_token_url="http://localhost:8080/token",
    oidc_client_id="it-platform-poller",
    oidc_client_secret="secret",
    amqp_url="amqp://poller:secret@localhost:5672/",
    heartbeat_exchange="Contracts.Events:PollerHeartbeat",
    http_timeout_seconds=10.0,
)


class FakeApi:
    """Records what the cycle asked for and answers with whatever the test queued."""

    def __init__(self, responses: list[dict[str, Any] | Exception]) -> None:
        self.responses = responses
        self.requested_versions: list[int | None] = []
        self.registrations = 0

    async def register(self) -> dict[str, Any]:
        self.registrations += 1
        return {"name": SETTINGS.name}

    async def fetch_config(self, since_version: int | None) -> dict[str, Any]:
        self.requested_versions.append(since_version)
        answer = self.responses.pop(0)
        if isinstance(answer, Exception):
            raise answer
        return answer


class FailingRegistrationApi(FakeApi):
    async def register(self) -> dict[str, Any]:
        self.registrations += 1
        raise RuntimeError("the API is restarting")


class FakePublisher:
    def __init__(self, fail_times: int = 0) -> None:
        self.published: list[dict[str, Any]] = []
        self.fail_times = fail_times

    async def publish(self, envelope: dict[str, Any]) -> None:
        if self.fail_times > 0:
            self.fail_times -= 1
            raise RuntimeError("the broker is unreachable")
        self.published.append(envelope)


async def test_run_cycle_first_cycle_registers_and_asks_for_a_full_snapshot() -> None:
    api = FakeApi([snapshot("a", "b", version=4)])
    publisher = FakePublisher()
    agent = PollerAgent(SETTINGS, api, publisher)

    await agent.run_cycle()

    assert api.registrations == 1
    assert api.requested_versions == [None]
    assert agent.state.version == 4
    assert len(publisher.published) == 1


async def test_run_cycle_second_cycle_asks_only_for_what_changed() -> None:
    api = FakeApi([snapshot("a", version=4), delta(version=6, upserted=["b"])])
    agent = PollerAgent(SETTINGS, api, FakePublisher())

    await agent.run_cycle()
    await agent.run_cycle()

    # Registration is an upsert, but repeating it every cycle would be noise; the version carried
    # between cycles is what keeps a steady state nearly empty.
    assert api.registrations == 1
    assert api.requested_versions == [None, 4]
    assert agent.state.version == 6


async def test_heartbeat_carries_the_envelope_masstransit_routes_on() -> None:
    api = FakeApi([snapshot("a", "b", version=4)])
    publisher = FakePublisher()
    agent = PollerAgent(SETTINGS, api, publisher)

    await agent.run_cycle()

    envelope = publisher.published[0]
    assert envelope["messageType"] == [HEARTBEAT_MESSAGE_URN]
    message = envelope["message"]
    assert message["pollerName"] == "poller-1"
    assert message["pollerGroup"] == "default"
    assert message["configVersion"] == 4
    assert message["deviceCount"] == 2
    assert message["intervalSeconds"] == 15
    assert message["cycleNumber"] == 1
    # The consumer reads this as a .NET record, so every value has to survive a JSON round trip.
    assert json.loads(json.dumps(envelope)) == envelope


async def test_run_cycle_config_fetch_failure_keeps_the_configuration_already_held() -> None:
    api = FakeApi([snapshot("a", "b", version=4), RuntimeError("connection reset")])
    publisher = FakePublisher()
    agent = PollerAgent(SETTINGS, api, publisher)

    await agent.run_cycle()
    await agent.run_cycle()

    # The failure path that matters: the estate is still there, so the poller must not forget it —
    # and it must still say it is alive, or the platform reports an outage that never happened.
    assert set(agent.state.devices) == {"a", "b"}
    assert agent.state.version == 4
    assert len(publisher.published) == 2
    assert publisher.published[1]["message"]["deviceCount"] == 2


async def test_run_cycle_rejected_version_takes_a_full_snapshot_immediately() -> None:
    api = FakeApi([
        snapshot("a", version=40),
        ConfigVersionRejectedError("sinceVersion 40 is ahead of the current version 2"),
        snapshot("z", version=2),
    ])
    agent = PollerAgent(SETTINGS, api, FakePublisher())

    await agent.run_cycle()
    await agent.run_cycle()

    assert api.requested_versions == [None, 40, None]
    assert set(agent.state.devices) == {"z"}
    assert agent.state.version == 2


async def test_run_cycle_unknown_poller_re_registers_next_cycle() -> None:
    api = FakeApi([
        PollerNotRegisteredError("no such poller"),
        snapshot("a", version=1),
    ])
    agent = PollerAgent(SETTINGS, api, FakePublisher())

    await agent.run_cycle()
    await agent.run_cycle()

    assert api.registrations == 2


async def test_run_cycle_registration_failure_does_not_stop_the_cycle() -> None:
    api = FailingRegistrationApi([RuntimeError("the API is restarting")])
    publisher = FakePublisher()
    agent = PollerAgent(SETTINGS, api, publisher)

    await agent.run_cycle()

    assert api.registrations == 1
    assert len(publisher.published) == 1


async def test_run_cycle_publish_failure_does_not_stop_the_cycle() -> None:
    api = FakeApi([snapshot("a", version=1), delta(version=2, upserted=["b"])])
    publisher = FakePublisher(fail_times=1)
    agent = PollerAgent(SETTINGS, api, publisher)

    await agent.run_cycle()
    await agent.run_cycle()

    # One missed beat is what the platform's two-beat threshold tolerates; crashing here would turn
    # a broker hiccup into a real outage.
    assert len(publisher.published) == 1
    assert publisher.published[0]["message"]["cycleNumber"] == 2


@pytest.mark.parametrize("cycles", [1, 3])
async def test_cycle_number_increments_once_per_cycle(cycles: int) -> None:
    responses: list[dict[str, Any] | Exception] = [snapshot("a", version=1)]
    responses += [delta(version=1) for _ in range(cycles - 1)]
    api = FakeApi(responses)
    agent = PollerAgent(SETTINGS, api, FakePublisher())

    for _ in range(cycles):
        await agent.run_cycle()

    assert agent.cycle_number == cycles
