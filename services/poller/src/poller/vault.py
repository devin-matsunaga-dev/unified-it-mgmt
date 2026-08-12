"""Credential material fetched from the platform vault, and how a check gets to use it.

The material never travels in the configuration document. A check carries a `credentialId`; the
poller notices which credentials its checks need, asks the platform for a short-lived grant, spends
it, and holds the plaintext in this process's memory only. Nothing here writes to disk and nothing
here is logged — the field *names* are, because "this credential has no community field" is a
diagnosis somebody needs and the value is not.
"""

from __future__ import annotations

import logging
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass, field, replace
from typing import Any

from .scheduler import DueCheck

logger = logging.getLogger("poller.vault")


@dataclass(frozen=True, slots=True)
class HeldCredential:
    """One credential this poller currently holds, at the version it was released at."""

    credential_id: str
    name: str
    kind: str
    version: int
    material: Mapping[str, str]

    def __repr__(self) -> str:
        # Overridden because a dataclass repr prints every field, and this one has a secret in it.
        # A traceback, a `logger.debug("%s", credential)` or an `assert` in a future test would
        # otherwise put a community string or an SNMP v3 auth key into a log file.
        return (
            f"HeldCredential(credential_id={self.credential_id!r}, name={self.name!r}, "
            f"kind={self.kind!r}, version={self.version!r}, fields={sorted(self.material)!r})"
        )


@dataclass(frozen=True, slots=True)
class CredentialNeed:
    """What the platform says this poller's checks need, and at which version."""

    credential_id: str
    version: int


@dataclass(slots=True)
class CredentialStore:
    """
    The credentials this poller holds, keyed by id.

    Two jobs. It decides whether a fetch is needed at all — by comparing the versions the platform
    reports against the versions it holds — and it merges the material into a check's parameters at
    the moment the check runs. Everything else about a rotation follows from the first of those: the
    platform bumps a version, this store sees a number it does not have, and the next cycle asks.
    """

    _held: dict[str, HeldCredential] = field(default_factory=dict)
    #: Ids already reported as missing, so a credential nothing can fetch logs once rather than
    #: every cycle for as long as it is broken.
    _warned: set[str] = field(default_factory=set)

    def needs(self, scope: Sequence[CredentialNeed]) -> bool:
        """True when anything in `scope` is missing or is at a version this store does not hold."""
        return any(
            self._held.get(need.credential_id) is None
            or self._held[need.credential_id].version != need.version
            for need in scope
        )

    def remember(self, released: Iterable[HeldCredential]) -> list[str]:
        """Takes what a redemption returned. Answers the ids it now holds, for the log line."""
        stored: list[str] = []
        for credential in released:
            self._held[credential.credential_id] = credential
            self._warned.discard(credential.credential_id)
            stored.append(credential.credential_id)
        return stored

    def retain(self, credential_ids: set[str]) -> None:
        """
        Forgets every credential outside `credential_ids`.

        Called with the platform's own scope each cycle, for the reason `PollingEngine.retain`
        exists — but with a sharper edge here: a check that stops using a credential, or a device
        that moves to another poller, should take the secret out of this process rather than leave
        it resident for as long as the container runs.
        """
        for forgotten in self._held.keys() - credential_ids:
            del self._held[forgotten]
            self._warned.discard(forgotten)

    def forget(self) -> None:
        """Drops everything. Follows a configuration the server has disowned."""
        self._held.clear()
        self._warned.clear()

    def apply(self, due: DueCheck) -> DueCheck:
        """
        Returns the check with its credential's material merged over its parameters.

        The material wins where the two collide, which is what makes a rotation take effect: a stale
        plaintext `community` left on a check must not be able to outrank the vault. A check with no
        credential, or one whose material this poller could not fetch, is returned untouched — it
        then runs with whatever it has and fails against a device that expects otherwise, which is a
        failed check with a reason rather than a silently wrong reading.
        """
        if not due.credential_id:
            return due

        held = self._held.get(due.credential_id)
        if held is None:
            if due.credential_id not in self._warned:
                self._warned.add(due.credential_id)
                logger.warning(
                    "Check has no credential material and will run unauthenticated.",
                    extra={
                        "check": due.check_id,
                        "device": due.device_id,
                        "credential": due.credential_id,
                    },
                )
            return due

        return replace(due, parameters={**due.parameters, **held.material})

    @property
    def held_ids(self) -> set[str]:
        return set(self._held)


def parse_scope(payload: Mapping[str, Any]) -> list[CredentialNeed]:
    """Reads the platform's credential-scope response. Metadata only; there is no material in it."""
    needs: list[CredentialNeed] = []
    for entry in payload.get("credentials") or []:
        credential_id = str(entry.get("id") or "")
        if not credential_id:
            continue
        try:
            version = int(entry.get("version") or 0)
        except (TypeError, ValueError):
            continue
        needs.append(CredentialNeed(credential_id=credential_id, version=version))
    return needs


def parse_released(payload: Mapping[str, Any]) -> list[HeldCredential]:
    """Reads a redemption response into held credentials, skipping anything malformed."""
    released: list[HeldCredential] = []
    for entry in payload.get("credentials") or []:
        credential_id = str(entry.get("id") or "")
        material = entry.get("material")
        if not credential_id or not isinstance(material, dict):
            continue
        try:
            version = int(entry.get("version") or 0)
        except (TypeError, ValueError):
            continue
        released.append(HeldCredential(
            credential_id=credential_id,
            name=str(entry.get("name") or ""),
            kind=str(entry.get("kind") or ""),
            version=version,
            material={str(key): str(value) for key, value in material.items()},
        ))
    return released
