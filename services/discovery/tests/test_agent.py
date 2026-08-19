from __future__ import annotations

from typing import Any

from discovery.agent import DiscoveryAgent
from discovery.config import ScanProfile
from discovery.scanner import DiscoveredDevice, ScanOutcome, ScanProgress
from discovery.settings import Settings

from .test_settings import REQUIRED


def settings() -> Settings:
    return Settings.from_env(REQUIRED | {"DISCOVERY_SNMP_COMMUNITIES": "healthy"})


def config(*profile_ids: str) -> dict[str, Any]:
    return {
        "discoveryGroup": "default",
        "generatedAt": "2026-08-13T00:00:00+00:00",
        "profiles": [
            {
                "scanProfileId": profile_id,
                "name": f"Profile {profile_id}",
                "ranges": ["10.0.0.0/30"],
                "ports": [],
                "intervalSeconds": 300,
                "timeoutSeconds": 2,
                "snmpEnabled": True,
                "neighbourDiscoveryEnabled": True,
            }
            for profile_id in profile_ids
        ],
    }


class FakeApi:
    def __init__(
        self,
        *responses: dict[str, Any],
        failing: bool = False,
        runs: list[dict[str, Any]] | None = None,
        runs_failing: bool = False,
    ) -> None:
        self._responses = list(responses)
        self._failing = failing
        self._runs = runs or []
        self._runs_failing = runs_failing
        self.calls = 0
        self.run_calls = 0
        self.reported: list[tuple[str, dict[str, Any]]] = []
        self.progress: list[tuple[str, dict[str, Any]]] = []

    async def fetch_scan_profiles(self) -> dict[str, Any]:
        self.calls += 1
        if self._failing:
            raise RuntimeError("Connection refused")
        return self._responses[min(self.calls - 1, len(self._responses) - 1)]

    async def fetch_scan_runs(self) -> dict[str, Any]:
        self.run_calls += 1
        if self._runs_failing:
            raise RuntimeError("Connection refused")
        # Claimed once, like the real endpoint: the platform moves the row on the way out, so a
        # second cycle is handed nothing.
        runs, self._runs = self._runs, []
        return {
            "discoveryGroup": "default",
            "generatedAt": "2026-08-19T00:00:00+00:00",
            "runs": runs,
        }

    async def report_scan_run(
        self, scan_run_id: str, result: dict[str, Any]
    ) -> dict[str, Any] | None:
        self.reported.append((scan_run_id, result))
        return {"id": scan_run_id}

    async def report_scan_progress(
        self, scan_run_id: str, progress: dict[str, Any]
    ) -> dict[str, Any] | None:
        self.progress.append((scan_run_id, progress))
        return {"id": scan_run_id}


class FakeScanner:
    """Answers with one device per profile, and records which profiles it was asked to scan."""

    def __init__(self, devices: int = 1, failing: bool = False) -> None:
        self._devices = devices
        self._failing = failing
        self.scanned: list[str] = []

    async def scan(
        self,
        profile: ScanProfile,
        on_progress: ScanProgress | None = None,
    ) -> ScanOutcome:
        self.scanned.append(profile.profile_id)
        if on_progress is not None:
            # Stand in for a sweep reporting itself, so the agent's plumbing is exercised.
            on_progress(2, 2, "10.0.0.1")
        if self._failing:
            raise RuntimeError("The sweep exploded")
        return ScanOutcome(
            profile_id=profile.profile_id,
            profile_name=profile.name,
            scan_id="0199c0de-4100-7000-8000-000000005ca4",
            addresses_probed=2,
            devices=tuple(
                DiscoveredDevice(address=f"10.0.0.{index}", responded_to_ping=True)
                for index in range(1, self._devices + 1)
            ),
        )


class RecordingPublisher:
    def __init__(self, failing: bool = False) -> None:
        self._failing = failing
        self.published: list[dict[str, Any]] = []

    async def publish(self, envelope: dict[str, Any]) -> None:
        if self._failing:
            raise RuntimeError("Broker unreachable")
        self.published.append(envelope)


def agent(
    api: FakeApi,
    scanner: FakeScanner,
    publisher: RecordingPublisher | None = None,
) -> DiscoveryAgent:
    return DiscoveryAgent(
        settings(),
        api,
        scanner,
        publisher=publisher,
    )


async def test_run_cycle_fetches_profiles_scans_them_and_publishes_each_device() -> None:
    publisher = RecordingPublisher()
    scanner = FakeScanner(devices=2)

    await agent(FakeApi(config("a")), scanner, publisher).run_cycle()

    assert scanner.scanned == ["a"]
    # One message per device, not one per scan: the consumer's unit of work is a device.
    assert len(publisher.published) == 2
    assert publisher.published[0]["messageType"] == [
        "urn:message:Contracts.Events:DeviceDiscovered"]
    assert publisher.published[0]["message"]["scanProfileName"] == "Profile a"


async def test_run_cycle_scans_a_profile_only_once_per_its_own_interval() -> None:
    scanner = FakeScanner()
    running = agent(FakeApi(config("a")), scanner, RecordingPublisher())

    await running.run_cycle()
    await running.run_cycle()

    # A five-minute profile on a thirty-second cycle costs nine cycles of nothing, which is the
    # whole point of the profile carrying its own interval.
    assert scanner.scanned == ["a"]


async def test_run_cycle_scans_a_newly_added_profile_in_the_cycle_that_learns_about_it() -> None:
    scanner = FakeScanner()
    running = agent(FakeApi(config("a"), config("a", "b")), scanner, RecordingPublisher())

    await running.run_cycle()
    await running.run_cycle()

    assert scanner.scanned == ["a", "b"]


async def test_run_cycle_stops_scanning_a_profile_that_left_the_configuration() -> None:
    scanner = FakeScanner()
    running = agent(FakeApi(config("a"), config()), scanner, RecordingPublisher())

    await running.run_cycle()
    await running.run_cycle()

    assert running.state.profiles == {}
    assert scanner.scanned == ["a"]


async def test_run_cycle_a_failed_config_fetch_keeps_the_profiles_already_held() -> None:
    scanner = FakeScanner()
    api = FakeApi(config("a"))
    running = agent(api, scanner, RecordingPublisher())
    await running.run_cycle()

    api._failing = True
    await running.run_cycle()

    # A scanner that forgot its profiles because one request timed out would stop scanning an
    # estate that is still there.
    assert set(running.state.profiles) == {"a"}


async def test_run_cycle_a_scan_that_raises_does_not_stop_the_next_profile() -> None:
    class HalfFailingScanner(FakeScanner):
        async def scan(
            self,
            profile: ScanProfile,
            on_progress: ScanProgress | None = None,
        ) -> ScanOutcome:
            self.scanned.append(profile.profile_id)
            if profile.profile_id == "a":
                raise RuntimeError("The sweep exploded")
            return ScanOutcome(
                profile_id=profile.profile_id,
                profile_name=profile.name,
                scan_id="0199c0de-4100-7000-8000-000000005ca4",
                addresses_probed=2,
                devices=(DiscoveredDevice(address="10.0.0.1", responded_to_ping=True),),
            )

    scanner = HalfFailingScanner()
    publisher = RecordingPublisher()

    await agent(FakeApi(config("a", "b")), scanner, publisher).run_cycle()

    assert scanner.scanned == ["a", "b"]
    assert len(publisher.published) == 1


async def test_run_cycle_a_failed_publish_does_not_stop_the_cycle() -> None:
    scanner = FakeScanner(devices=2)

    # A discovery that could not be published is lost on purpose: the profile runs again on its own
    # schedule, and a scanner that queued findings through a broker outage would come back and
    # publish an hour of them all stamped as if the estate had just changed.
    await agent(FakeApi(config("a")), scanner, RecordingPublisher(failing=True)).run_cycle()

    assert scanner.scanned == ["a"]


async def test_run_cycle_with_no_profiles_scans_nothing_and_publishes_nothing() -> None:
    scanner = FakeScanner()
    publisher = RecordingPublisher()

    # The first cycle of a freshly deployed scanner nobody has written a profile for.
    await agent(FakeApi(config()), scanner, publisher).run_cycle()

    assert scanner.scanned == []
    assert publisher.published == []


async def test_run_cycle_a_scan_that_found_nothing_publishes_nothing() -> None:
    scanner = FakeScanner(devices=0)
    publisher = RecordingPublisher()

    await agent(FakeApi(config("a")), scanner, publisher).run_cycle()

    # The WP's empty-range case, from the agent's side: the scan ran, found nothing, and said so in
    # a log line rather than on the bus.
    assert scanner.scanned == ["a"]
    assert publisher.published == []


async def test_run_cycle_counts_cycles_even_when_everything_fails() -> None:
    running = agent(FakeApi(config("a"), failing=True), FakeScanner(failing=True))

    await running.run_cycle()
    await running.run_cycle()

    assert running.cycle_number == 2


# --- the schedule switches, and the on-demand runs they exist beside (Phase 5.5) ---


def scheduled_config(
    *profile_ids: str, enabled: bool = True, scheduled: bool = True,
) -> dict[str, Any]:
    """A config document with the two switches set explicitly rather than left to default."""
    document = config(*profile_ids)
    document["scheduledScanningEnabled"] = enabled
    for profile in document["profiles"]:
        profile["scheduleEnabled"] = scheduled
    return document


def run(scan_run_id: str, profile_id: str, ranges: list[str] | None = None) -> dict[str, Any]:
    return {
        "scanRunId": scan_run_id,
        "deadlineAt": "2026-08-19T00:30:00+00:00",
        "profile": {
            "scanProfileId": profile_id,
            "name": f"Profile {profile_id}",
            "ranges": ["10.0.0.0/30"] if ranges is None else ranges,
            "ports": [],
            "intervalSeconds": 300,
            "timeoutSeconds": 2,
            "snmpEnabled": True,
            "neighbourDiscoveryEnabled": True,
            "scheduleEnabled": False,
        },
    }


async def test_run_cycle_estate_switch_off_stops_every_scheduled_scan() -> None:
    scanner = FakeScanner()

    await agent(FakeApi(scheduled_config("a", "b", enabled=False)), scanner).run_cycle()

    # The kill switch is aimed at the clock: nothing ran, and the profiles are still held.
    assert scanner.scanned == []


async def test_run_cycle_a_profile_with_its_schedule_off_is_held_but_never_started() -> None:
    scanner = FakeScanner()
    document = scheduled_config("a", "b")
    document["profiles"][1]["scheduleEnabled"] = False

    running = agent(FakeApi(document), scanner)
    await running.run_cycle()

    assert scanner.scanned == ["a"]
    # Held, not forgotten — this is what makes it runnable on demand.
    assert set(running.state.profiles) == {"a", "b"}


async def test_run_cycle_runs_a_requested_scan_publishes_it_and_reports_what_it_found() -> None:
    publisher = RecordingPublisher()
    scanner = FakeScanner(devices=2)
    api = FakeApi(scheduled_config(enabled=False), runs=[run("run-1", "on-demand")])

    await agent(api, scanner, publisher).run_cycle()

    # The whole point of the switch split: scheduled scanning is off and this still ran.
    assert scanner.scanned == ["on-demand"]
    assert len(publisher.published) == 2
    assert api.reported == [("run-1", {
        "outcome": "Succeeded",
        "addressesProbed": 2,
        "devicesFound": 2,
        "error": None,
    })]


async def test_run_cycle_a_requested_scan_that_raises_is_reported_failed_and_not_retried() -> None:
    scanner = FakeScanner(failing=True)
    api = FakeApi(config(), runs=[run("run-1", "on-demand")])
    running = agent(api, scanner)

    await running.run_cycle()
    await running.run_cycle()

    assert [entry[0] for entry in api.reported] == ["run-1"]
    assert api.reported[0][1]["outcome"] == "Failed"
    # Never retried: the second cycle is handed nothing, and nothing re-runs from memory.
    assert scanner.scanned == ["on-demand"]


async def test_run_cycle_a_requested_scan_with_an_unusable_profile_is_reported_not_run() -> None:
    scanner = FakeScanner()
    api = FakeApi(config(), runs=[run("run-1", "on-demand", ranges=[])])

    await agent(api, scanner).run_cycle()

    assert scanner.scanned == []
    assert api.reported[0][1]["outcome"] == "Failed"


async def test_run_cycle_survives_the_requested_scan_fetch_failing() -> None:
    scanner = FakeScanner()
    api = FakeApi(config("a"), runs_failing=True)

    # The config fetch's rule, applied to the second endpoint: a scanner that stopped because the
    # API blinked is one somebody has to go and start again.
    await agent(api, scanner).run_cycle()

    assert scanner.scanned == ["a"]


async def test_run_cycle_a_requested_scan_reports_its_progress_while_it_runs() -> None:
    api = FakeApi(config(), runs=[run("run-1", "on-demand")])

    await agent(api, FakeScanner()).run_cycle()

    # The evidence a person watches: how many addresses are done, out of how many, and the last
    # one that answered. There is deliberately no "currently scanning" — hundreds are in flight.
    assert api.progress == [("run-1", {
        "addressesProbed": 2,
        "addressesTotal": 2,
        "lastRespondingAddress": "10.0.0.1",
    })]


async def test_run_cycle_a_scheduled_scan_reports_no_progress() -> None:
    api = FakeApi(config("a"))

    await agent(api, FakeScanner()).run_cycle()

    # Nobody is watching a scheduled sweep, and a progress post per second per profile would be a
    # steady stream of writes for an audience of none.
    assert api.progress == []
