from __future__ import annotations

from typing import Any

import pytest

from discovery.pysnmp_transport import render


class FakeOid:
    """An OID-valued var-bind, as pysnmp hands one back: `prettyPrint` resolves it against a MIB."""

    def __init__(self, dotted: str, symbolic: str) -> None:
        self._dotted = dotted
        self._symbolic = symbolic

    def asTuple(self) -> tuple[int, ...]:  # noqa: N802 — pysnmp's own spelling
        return tuple(int(part) for part in self._dotted.split("."))

    def prettyPrint(self) -> str:  # noqa: N802 — pysnmp's own spelling
        return self._symbolic


class FakeOctetString:
    """A string-valued var-bind. `asNumbers`, not `asTuple`, which is what keeps them apart."""

    def __init__(self, text: str) -> None:
        self._text = text

    def asNumbers(self) -> tuple[int, ...]:  # noqa: N802 — pysnmp's own spelling
        return tuple(self._text.encode())

    def prettyPrint(self) -> str:  # noqa: N802 — pysnmp's own spelling
        return self._text


class FakeInteger:
    def __init__(self, value: int) -> None:
        self._value = value

    def prettyPrint(self) -> str:  # noqa: N802 — pysnmp's own spelling
        return str(self._value)


def test_render_an_oid_is_numeric_even_when_a_mib_resolves_it() -> None:
    value = FakeOid("1.3.6.1.4.1.8072.3.2.10", "SNMPv2-SMI::enterprises.8072.3.2.10")

    # Found live: the simulator's sysObjectID arrived MIB-resolved, so the field's format depended
    # on which modules were installed beside the scanner rather than on the device. sysObjectID is
    # the fingerprint WP-4.2 matches on, and a key that renders two ways for a device is not a key.
    assert render(value) == "1.3.6.1.4.1.8072.3.2.10"


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        (FakeOctetString("IT Platform simulated switch, healthy profile"),
         "IT Platform simulated switch, healthy profile"),
        (FakeOctetString("00:1b:0d:aa:bb:01"), "00:1b:0d:aa:bb:01"),
        (FakeInteger(518_400_000), "518400000"),
    ],
)
def test_render_everything_else_keeps_pysnmps_own_rendering(value: Any, expected: str) -> None:
    # An OCTET STRING has `asNumbers` and an INTEGER has neither, so neither is caught by the OID
    # branch — which is the whole risk of detecting a type by the methods it carries.
    assert render(value) == expected


def test_render_a_value_whose_astuple_raises_falls_back_rather_than_failing() -> None:
    class Awkward:
        def asTuple(self) -> tuple[int, ...]:  # noqa: N802 — pysnmp's own spelling
            raise RuntimeError("not really an OID")

        def prettyPrint(self) -> str:  # noqa: N802 — pysnmp's own spelling
            return "still readable"

    # One agent's odd answer must not fail the read that found it.
    assert render(Awkward()) == "still readable"
