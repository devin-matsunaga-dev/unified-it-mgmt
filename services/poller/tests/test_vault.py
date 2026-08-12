from __future__ import annotations

import logging

from poller.scheduler import DueCheck
from poller.vault import (
    CredentialNeed,
    CredentialStore,
    HeldCredential,
    parse_released,
    parse_scope,
)


def held(credential_id: str = "cred-1", version: int = 1, **material: str) -> HeldCredential:
    return HeldCredential(
        credential_id=credential_id,
        name="Simulator SNMP",
        kind="SnmpV2c",
        version=version,
        material=material or {"community": "healthy"},
    )


def due(credential_id: str = "cred-1", **parameters: str) -> DueCheck:
    return DueCheck(
        device_id="device-1",
        ci_id="ci-1",
        address="snmpsim",
        check_id="check-1",
        check_type="Snmp",
        check_name="SNMP: CPU",
        interval_seconds=60,
        timeout_seconds=5.0,
        parameters=parameters or {"metric": "cpu", "version": "2c"},
        credential_id=credential_id,
    )


def test_needs_a_credential_it_has_never_held() -> None:
    store = CredentialStore()

    assert store.needs([CredentialNeed("cred-1", version=1)]) is True


def test_needs_nothing_once_it_holds_the_version_the_platform_reports() -> None:
    store = CredentialStore()
    store.remember([held(version=3)])

    assert store.needs([CredentialNeed("cred-1", version=3)]) is False


def test_needs_the_credential_again_after_it_is_rotated() -> None:
    """The whole rotation protocol: the platform moves a number and the poller notices."""
    store = CredentialStore()
    store.remember([held(version=3)])

    assert store.needs([CredentialNeed("cred-1", version=4)]) is True


def test_apply_merges_material_over_the_checks_own_parameters() -> None:
    store = CredentialStore()
    store.remember([held(community="healthy")])

    applied = store.apply(due())

    assert applied.parameters["community"] == "healthy"
    # Everything the check configured is still there; the vault adds, it does not replace.
    assert applied.parameters["metric"] == "cpu"


def test_apply_lets_the_vault_win_over_a_stale_plaintext_parameter() -> None:
    """
    A check edited before WP-3.11 may still carry a plaintext community beside its credential id.
    The vault's copy has to outrank it, or a rotation would take effect for nobody.
    """
    store = CredentialStore()
    store.remember([held(community="rotated")])

    applied = store.apply(due(community="stale", metric="cpu"))

    assert applied.parameters["community"] == "rotated"


def test_apply_leaves_a_check_with_no_credential_untouched() -> None:
    store = CredentialStore()

    check = due(credential_id="")
    assert store.apply(check) is check


def test_apply_warns_once_when_material_is_missing_and_runs_unauthenticated() -> None:
    """
    The failure path. A credential the poller could not fetch must not stop the check being run —
    it runs unauthenticated, fails against a device that expects otherwise, and says so once rather
    than once per cycle for as long as the vault is unreachable.
    """
    store = CredentialStore()

    with _capture("poller.vault") as records:
        first = store.apply(due())
        store.apply(due())

    assert "community" not in first.parameters
    assert len(records) == 1


def test_retain_drops_a_credential_that_left_this_pollers_scope() -> None:
    """A secret for a device this poller no longer polls must not stay resident for months."""
    store = CredentialStore()
    store.remember([held("cred-1"), held("cred-2")])

    store.retain({"cred-1"})

    assert store.held_ids == {"cred-1"}


def test_forget_drops_everything() -> None:
    store = CredentialStore()
    store.remember([held()])

    store.forget()

    assert store.held_ids == set()


def test_repr_never_prints_the_secret() -> None:
    """
    A dataclass repr prints every field. This one is overridden precisely so that a traceback or a
    stray debug log cannot put a community string into a log file.
    """
    text = repr(held(community="s3cr3t"))

    assert "s3cr3t" not in text
    assert "community" in text


def test_parse_scope_reads_ids_and_versions_and_skips_malformed_entries() -> None:
    needs = parse_scope({"credentials": [
        {"id": "cred-1", "name": "a", "kind": "SnmpV2c", "version": 2},
        {"id": "", "version": 1},
        {"id": "cred-2", "version": "not a number"},
    ]})

    assert needs == [CredentialNeed("cred-1", version=2)]


def test_parse_released_skips_an_entry_with_no_material() -> None:
    released = parse_released({"credentials": [
        {"id": "cred-1", "name": "a", "kind": "SnmpV2c", "version": 1,
         "material": {"community": "healthy"}},
        {"id": "cred-2", "name": "b", "kind": "SnmpV2c", "version": 1},
    ]})

    assert [item.credential_id for item in released] == ["cred-1"]
    assert released[0].material == {"community": "healthy"}


class _RecordingHandler(logging.Handler):
    def __init__(self) -> None:
        super().__init__()
        self.records: list[logging.LogRecord] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.records.append(record)


class _Capture:
    """Collects one logger's records for the duration of a `with`. No pytest fixture needed."""

    def __init__(self, name: str) -> None:
        self._logger = logging.getLogger(name)
        self._handler = _RecordingHandler()

    def __enter__(self) -> list[logging.LogRecord]:
        self._logger.addHandler(self._handler)
        return self._handler.records

    def __exit__(self, *_: object) -> None:
        self._logger.removeHandler(self._handler)


def _capture(name: str) -> _Capture:
    return _Capture(name)
