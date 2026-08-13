"""The object identifiers a scan reads, named once so no other module spells one out."""

from __future__ import annotations

from typing import Final

# SNMPv2-MIB::system. Scalars, so each carries its .0 instance suffix. This is the identify: what a
# device says it is, which is the difference between a discovery worth reviewing and a bare IP.
SYS_DESCR: Final = "1.3.6.1.2.1.1.1.0"
SYS_OBJECT_ID: Final = "1.3.6.1.2.1.1.2.0"
SYS_UPTIME: Final = "1.3.6.1.2.1.1.3.0"
SYS_CONTACT: Final = "1.3.6.1.2.1.1.4.0"
SYS_NAME: Final = "1.3.6.1.2.1.1.5.0"
SYS_LOCATION: Final = "1.3.6.1.2.1.1.6.0"

#: sysUpTime is in hundredths of a second, which is a unit nobody wants to read on a screen.
TIMETICKS_PER_SECOND: Final = 100

# IF-MIB::ifName. Walked only when a neighbour was found, to turn CDP's interface *index* into the
# name an operator would recognise. LLDP carries its own port ids and needs no lookup.
IF_NAME: Final = "1.3.6.1.2.1.31.1.1.1.1"

# LLDP-MIB::lldpRemTable, the standard neighbour table. Its index is
# `timeMark.localPortNum.remIndex`, so the walk's key carries which local port saw the neighbour —
# that is why `lldpRemLocalPortNum` is never read as a column of its own.
LLDP_REM_CHASSIS_ID: Final = "1.0.8802.1.1.2.1.4.1.1.5"
LLDP_REM_PORT_ID: Final = "1.0.8802.1.1.2.1.4.1.1.7"
LLDP_REM_SYS_NAME: Final = "1.0.8802.1.1.2.1.4.1.1.9"

#: LLDP-MIB::lldpLocPortId, keyed by local port number: the reporting device's own interface name.
LLDP_LOC_PORT_ID: Final = "1.0.8802.1.1.2.1.3.7.1.3"

# CISCO-CDP-MIB::cdpCacheTable. Cisco's own neighbour discovery, still the only one a lot of
# estates have switched on. Indexed by `ifIndex.deviceIndex`, so the local interface needs IF_NAME
# above.
CDP_CACHE_ADDRESS: Final = "1.3.6.1.4.1.9.9.23.1.2.1.1.4"
CDP_CACHE_DEVICE_ID: Final = "1.3.6.1.4.1.9.9.23.1.2.1.1.6"
CDP_CACHE_DEVICE_PORT: Final = "1.3.6.1.4.1.9.9.23.1.2.1.1.7"
