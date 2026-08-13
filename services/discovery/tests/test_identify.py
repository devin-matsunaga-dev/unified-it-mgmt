from __future__ import annotations

from collections.abc import Sequence

from discovery import oids as oid
from discovery.identify import CDP, LLDP, identify, walk_neighbours
from discovery.snmp import SnmpError, SnmpTarget, SnmpValue

SYS_INFO = {
    oid.SYS_DESCR: "IT Platform simulated switch, healthy profile",
    oid.SYS_NAME: "sim-switch-healthy",
    oid.SYS_LOCATION: "Primary Data Centre",
    oid.SYS_CONTACT: "itops@example.com",
    oid.SYS_OBJECT_ID: "1.3.6.1.4.1.8072.3.2.10",
    oid.SYS_UPTIME: "518400000",
}


class FakeTransport:
    """
    Answers for one community and refuses every other, recording what it was asked.

    A scan meets strangers, so "the wrong community" is the normal case rather than an error path,
    and the ordering of the attempts is behaviour worth asserting.
    """

    def __init__(
        self,
        community: str | None = None,
        values: dict[str, SnmpValue] | None = None,
        walks: dict[str, dict[str, SnmpValue]] | None = None,
    ) -> None:
        self._community = community
        self._values = values if values is not None else dict(SYS_INFO)
        self._walks = walks or {}
        self.communities_tried: list[str] = []
        self.roots_walked: list[str] = []

    async def get(self, target: SnmpTarget, requested: Sequence[str]) -> dict[str, SnmpValue]:
        self.communities_tried.append(target.community)
        if self._community is not None and target.community != self._community:
            raise SnmpError(f"SNMP request to {target.host} failed: No SNMP response received")
        return {name: self._values[name] for name in requested if name in self._values}

    async def walk(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]:
        self.roots_walked.append(root)
        if self._community is not None and target.community != self._community:
            raise SnmpError(f"SNMP request to {target.host} failed: No SNMP response received")
        return dict(self._walks.get(root, {}))


async def test_identify_reads_the_system_group_the_device_answered_with() -> None:
    transport = FakeTransport(community="healthy")

    found = await identify("10.0.0.2", ["healthy"], transport)

    assert found is not None
    assert found.sys_name == "sim-switch-healthy"
    assert found.sys_description == "IT Platform simulated switch, healthy profile"
    assert found.sys_object_id == "1.3.6.1.4.1.8072.3.2.10"
    assert found.sys_location == "Primary Data Centre"
    assert found.sys_contact == "itops@example.com"
    # sysUpTime is hundredths of a second, which is a unit nobody wants to read.
    assert found.uptime_seconds == 5_184_000.0
    assert found.community == "healthy"


async def test_identify_tries_communities_in_order_and_stops_at_the_first_that_answers() -> None:
    transport = FakeTransport(community="degraded")

    found = await identify("10.0.0.2", ["healthy", "degraded", "public"], transport)

    assert found is not None
    assert found.community == "degraded"
    # Ordered rather than concurrent: firing every community at a stranger at once is what an SNMP
    # brute-force looks like in an IDS log. And it stops — `public` is never tried.
    assert transport.communities_tried == ["healthy", "degraded"]


async def test_identify_an_address_with_no_agent_is_not_an_error() -> None:
    transport = FakeTransport(community="healthy")

    # Most addresses in a range have no SNMP agent at all. The discovery still reports the ping and
    # the open ports, so this has to be None rather than a raise.
    assert await identify("10.0.0.9", ["wrong", "alsowrong"], transport) is None


async def test_identify_an_agent_that_answers_with_nothing_is_not_an_identity() -> None:
    # An agent that answers the socket but every scalar empty has identified itself as nothing, and
    # reporting that would put a nameless device into a review queue as though the scan learned
    # something.
    transport = FakeTransport(community="healthy", values={})

    assert await identify("10.0.0.2", ["healthy"], transport) is None


async def test_identify_an_agent_that_answers_only_sysdescr_is_still_an_identity() -> None:
    # The common case on real hardware, and sysDescr alone is what tells a router from a printer.
    transport = FakeTransport(community="healthy", values={oid.SYS_DESCR: "Cisco IOS Software"})

    found = await identify("10.0.0.2", ["healthy"], transport)

    assert found is not None
    assert found.sys_description == "Cisco IOS Software"
    assert found.sys_name is None


async def test_walk_neighbours_reads_lldp_with_the_local_port_out_of_the_index() -> None:
    transport = FakeTransport(
        community="healthy",
        walks={
            # Index is timeMark.localPortNum.remIndex, so the local port is already in the key.
            oid.LLDP_REM_SYS_NAME: {"0.1.1": "dc1-core-rtr-01", "0.2.1": "dc1-core-sw-02"},
            oid.LLDP_REM_PORT_ID: {"0.1.1": "GigabitEthernet0/24", "0.2.1": "GigabitEthernet0/23"},
            oid.LLDP_REM_CHASSIS_ID: {"0.1.1": "00:1b:0d:aa:bb:01", "0.2.1": "00:1b:0d:aa:bb:02"},
            oid.LLDP_LOC_PORT_ID: {"1": "GigabitEthernet0/1", "2": "GigabitEthernet0/2"},
        },
    )

    neighbours = await walk_neighbours("10.0.0.2", "healthy", transport)

    assert [item.protocol for item in neighbours] == [LLDP, LLDP]
    assert neighbours[0].local_port == "GigabitEthernet0/1"
    assert neighbours[0].remote_system_name == "dc1-core-rtr-01"
    assert neighbours[0].remote_port == "GigabitEthernet0/24"
    assert neighbours[1].local_port == "GigabitEthernet0/2"
    assert neighbours[1].remote_system_name == "dc1-core-sw-02"


async def test_walk_neighbours_falls_back_to_the_chassis_id_when_a_name_is_absent() -> None:
    transport = FakeTransport(
        community="healthy",
        walks={
            oid.LLDP_REM_CHASSIS_ID: {"0.1.1": "00:1b:0d:aa:bb:01"},
            oid.LLDP_LOC_PORT_ID: {"1": "GigabitEthernet0/1"},
        },
    )

    neighbours = await walk_neighbours("10.0.0.2", "healthy", transport)

    # The row survives: a chassis id is what makes the link traceable, and dropping it would hide a
    # cable somebody has to follow.
    assert len(neighbours) == 1
    assert neighbours[0].remote_system_name == "00:1b:0d:aa:bb:01"


async def test_walk_neighbours_reads_cdp_and_decodes_its_packed_address() -> None:
    transport = FakeTransport(
        community="healthy",
        walks={
            # cdpCacheTable is indexed by ifIndex.deviceIndex, so the local interface needs ifName.
            oid.CDP_CACHE_DEVICE_ID: {"3.1": "dc1-core-rtr-01"},
            oid.CDP_CACHE_DEVICE_PORT: {"3.1": "GigabitEthernet0/24"},
            oid.CDP_CACHE_ADDRESS: {"3.1": "0x0a000001"},
            oid.IF_NAME: {"3": "GigabitEthernet0/3"},
        },
    )

    neighbours = await walk_neighbours("10.0.0.2", "healthy", transport)

    assert len(neighbours) == 1
    assert neighbours[0].protocol == CDP
    assert neighbours[0].local_port == "GigabitEthernet0/3"
    # pysnmp renders the octet string as hex; an address nobody can read is worth nothing on a map.
    assert neighbours[0].remote_address == "10.0.0.1"


async def test_walk_neighbours_skips_the_cdp_walks_entirely_when_the_cache_is_empty() -> None:
    transport = FakeTransport(community="healthy", walks={})

    assert await walk_neighbours("10.0.0.2", "healthy", transport) == ()
    # Three round trips saved against every device with no CDP cache, which is most of them.
    assert oid.CDP_CACHE_ADDRESS not in transport.roots_walked
    assert oid.IF_NAME not in transport.roots_walked


async def test_walk_neighbours_on_a_device_with_no_lldp_mib_reports_none() -> None:
    class RefusingTransport(FakeTransport):
        async def walk(self, target: SnmpTarget, root: str) -> dict[str, SnmpValue]:
            raise SnmpError("The agent refused the request: noSuchName at index 1.")

    assert await walk_neighbours("10.0.0.2", "healthy", RefusingTransport()) == ()
