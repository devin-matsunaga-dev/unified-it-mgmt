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

#: sysUpTime is in hundredths of a second, which is a unit nobody wants to read on a chart.
TIMETICKS_PER_SECOND: Final = 100
