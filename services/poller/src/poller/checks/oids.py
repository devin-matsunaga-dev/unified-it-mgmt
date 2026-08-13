"""The object identifiers this poller reads, named once so no other module spells one out."""

from __future__ import annotations

from typing import Final

# SNMPv2-MIB::system. Scalars, so each carries its .0 instance suffix.
SYS_DESCR: Final = "1.3.6.1.2.1.1.1.0"
SYS_OBJECT_ID: Final = "1.3.6.1.2.1.1.2.0"
SYS_UPTIME: Final = "1.3.6.1.2.1.1.3.0"
SYS_CONTACT: Final = "1.3.6.1.2.1.1.4.0"
SYS_NAME: Final = "1.3.6.1.2.1.1.5.0"
SYS_LOCATION: Final = "1.3.6.1.2.1.1.6.0"

# HOST-RESOURCES-MIB. Columns, walked rather than fetched: a device has as many rows as it has
# processors and storage areas, and neither count is knowable in advance.
HR_PROCESSOR_LOAD: Final = "1.3.6.1.2.1.25.3.3.1.2"
HR_STORAGE_TYPE: Final = "1.3.6.1.2.1.25.2.3.1.2"
HR_STORAGE_DESCR: Final = "1.3.6.1.2.1.25.2.3.1.3"
HR_STORAGE_ALLOCATION_UNITS: Final = "1.3.6.1.2.1.25.2.3.1.4"
HR_STORAGE_SIZE: Final = "1.3.6.1.2.1.25.2.3.1.5"
HR_STORAGE_USED: Final = "1.3.6.1.2.1.25.2.3.1.6"

#: The hrStorageType value that means physical memory; the same table also holds disks and buffers.
HR_STORAGE_RAM: Final = "1.3.6.1.2.1.25.2.1.2"

# UCD-SNMP-MIB. The fallback for hosts that answer net-snmp's own MIB but carry no host-resources
# tables, which is most appliances and every stock net-snmp on a router.
UCD_CPU_IDLE: Final = "1.3.6.1.4.1.2021.11.11.0"
UCD_MEMORY_TOTAL_REAL: Final = "1.3.6.1.4.1.2021.4.5.0"
UCD_MEMORY_AVAILABLE_REAL: Final = "1.3.6.1.4.1.2021.4.6.0"

# IF-MIB. Two tables rather than fourteen columns: `interfaces` walks each table whole and picks the
# columns it wants out of the result, because a walk of the subtree is two round trips where a walk
# per column is fourteen, and a switch is asked for its interfaces every cycle forever.
IF_TABLE: Final = "1.3.6.1.2.1.2.2.1"
IF_X_TABLE: Final = "1.3.6.1.2.1.31.1.1.1"

#: Column numbers within `IF_TABLE`, which is what a walk of it returns keys of.
IF_DESCR: Final = 2
IF_TYPE: Final = 3
IF_SPEED: Final = 5
IF_PHYS_ADDRESS: Final = 6
IF_ADMIN_STATUS: Final = 7
IF_OPER_STATUS: Final = 8
IF_IN_OCTETS: Final = 10
IF_IN_DISCARDS: Final = 13
IF_IN_ERRORS: Final = 14
IF_OUT_OCTETS: Final = 16
IF_OUT_DISCARDS: Final = 19
IF_OUT_ERRORS: Final = 20

#: Column numbers within `IF_X_TABLE`. The octet counters here are 64-bit and are preferred: a
#: 32-bit counter wraps in 34 seconds on a 10 Gbit/s link, sooner than any polling interval.
IF_NAME: Final = 1
IF_HC_IN_OCTETS: Final = 6
IF_HC_OUT_OCTETS: Final = 10
IF_HIGH_SPEED: Final = 15
IF_ALIAS: Final = 18

#: ifHighSpeed is in megabits per second; everything this poller publishes about a link is in bits.
BITS_PER_MEGABIT: Final = 1_000_000

#: An octet is eight bits, and a link's speed is quoted in bits while its counters count octets.
BITS_PER_OCTET: Final = 8

#: sysUpTime is in hundredths of a second, which is a unit nobody wants to read on a chart.
TIMETICKS_PER_SECOND: Final = 100
