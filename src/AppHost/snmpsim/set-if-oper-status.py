"""
Shut a simulated switch port, or bring it back — the hand gesture WP-4.5's verification needs.

snmpsim serves a recording, so an interface's operational status is whatever the `.snmprec` file
says unless somebody writes to it. `healthy.snmprec` tags ifOperStatus on port 2 with snmpsim's
`writecache` module, which makes that one OID accept an SNMP SET and remember it in the simulator's
memory. This script is the SET.

It runs inside the poller's container, because that container already has pysnmp installed and is on
the network the simulator is on — the simulator deliberately publishes no host port (WP-3.3), so
there is nothing to aim an SNMP client on the host at:

    docker exec -i $(docker ps -qf name=poller | head -1) \\
        python - snmpsim 2 down < src/AppHost/snmpsim/set-if-oper-status.py

The change lasts until the container is restarted: `writecache` holds it in memory rather than
writing it back to the file, so a restarted simulator serves the recording again with every port up.
"""

from __future__ import annotations

import asyncio
import sys

from pysnmp.hlapi.v3arch.asyncio import (
    CommunityData,
    ContextData,
    Integer32,
    ObjectIdentity,
    ObjectType,
    SnmpEngine,
    UdpTransportTarget,
    set_cmd,
)

#: IF-MIB::ifOperStatus, the column. The instance is the interface's own index.
IF_OPER_STATUS = "1.3.6.1.2.1.2.2.1.8"

STATUSES = {"up": 1, "down": 2}

USAGE = (
    "usage: set-if-oper-status.py <host> <ifIndex> <up|down> [community] [port]\n"
    "example: set-if-oper-status.py snmpsim 2 down"
)


async def main(argv: list[str]) -> int:
    if len(argv) < 3 or argv[2].casefold() not in STATUSES:
        print(USAGE, file=sys.stderr)
        return 2

    host, index, wanted = argv[0], argv[1], argv[2].casefold()
    community = argv[3] if len(argv) > 3 else "healthy"
    port = int(argv[4]) if len(argv) > 4 else 161

    engine = SnmpEngine()
    try:
        error_indication, error_status, error_index, var_binds = await set_cmd(
            engine,
            CommunityData(community, mpModel=1),
            await UdpTransportTarget.create((host, port), timeout=3, retries=1),
            ContextData(),
            ObjectType(
                ObjectIdentity(f"{IF_OPER_STATUS}.{index}"),
                Integer32(STATUSES[wanted]),
            ),
        )
    finally:
        engine.close_dispatcher()

    if error_indication:
        print(f"The simulator at {host}:{port} did not answer: {error_indication}", file=sys.stderr)
        return 1
    if error_status:
        # The likeliest cause by far: the OID being written is not tagged `writecache` in the
        # recording, so the simulator is serving it read-only.
        print(
            f"The simulator refused the write: {error_status.prettyPrint()} at index {error_index}. "
            f"Is ifOperStatus.{index} tagged 'writecache' in the .snmprec?",
            file=sys.stderr,
        )
        return 1

    for name, value in var_binds:
        # A simulator serving this OID read-only answers the SET with `No Such Instance` and no error
        # status at all — probed against the image, and it is what a port other than the one tagged
        # `writecache` does. Reading the value back is the only way to tell that apart from a write
        # that worked.
        if value.prettyPrint() != str(STATUSES[wanted]):
            print(
                f"The simulator did not take the write: {name} answered "
                f"'{value.prettyPrint()}'. Is ifOperStatus.{index} tagged 'writecache' in the "
                f".snmprec? Only port 2 of the healthy profile is.",
                file=sys.stderr,
            )
            return 1
        print(f"{name} = {value.prettyPrint()} ({wanted})")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main(sys.argv[1:])))
