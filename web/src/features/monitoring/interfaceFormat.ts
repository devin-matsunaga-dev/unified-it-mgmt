import type { DeviceInterface, InterfaceStatus } from '../../api/monitoring'

/**
 * How an interface's numbers are written. Pure, and separate from the table, because these are the
 * decisions worth being sure about: a null is a measurement nobody has taken and must never render
 * as a zero, and a rate is quoted in the unit an operator would say out loud rather than in the bits
 * the MIB counts.
 */

/** What a cell shows when there is nothing to show. An em dash, never a 0. */
export const NOT_MEASURED = '—'

const BIT_UNITS = ['bit/s', 'kbit/s', 'Mbit/s', 'Gbit/s', 'Tbit/s']

/**
 * A rate in the largest unit that leaves a number worth reading.
 *
 * Decimal steps of 1000, not 1024: a link is sold in decimal bits — a "gigabit" port is 1,000,000,000
 * bit/s and its utilisation is computed against that, so scaling by 1024 here would make a saturated
 * port read as 93%.
 */
export function formatBitsPerSecond(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return NOT_MEASURED

  let scaled = value
  let unit = 0
  while (Math.abs(scaled) >= 1000 && unit < BIT_UNITS.length - 1) {
    scaled /= 1000
    unit += 1
  }

  // One decimal place while the number is small enough for it to mean something, none above 100 —
  // "847 Mbit/s" is easier to compare down a column than "846.7 Mbit/s".
  const decimals = unit === 0 || Math.abs(scaled) >= 100 ? 0 : 1
  // A trailing ".0" is noise in a column of rates and reads oddly beside a port's rated speed —
  // "1 Gbit/s" is how the link is sold and how an operator says it.
  return `${scaled.toFixed(decimals).replace(/\.0$/, '')} ${BIT_UNITS[unit]}`
}

/** A link's rated speed. The same scale, and "—" for a port that reports none (usually a shut one). */
export function formatSpeed(value: number | null | undefined): string {
  return formatBitsPerSecond(value)
}

export function formatUtilisation(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return NOT_MEASURED
  return `${value.toFixed(1)}%`
}

/**
 * Errors and discards, which are counted per second and are almost always zero.
 *
 * Two decimal places, because the interesting case is small: one CRC error a minute is 0.02/s and a
 * column that rounded it to 0 would say the cable is fine.
 */
export function formatEventRate(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return NOT_MEASURED
  if (value === 0) return '0'
  return value < 0.01 ? '<0.01/s' : `${value.toFixed(2)}/s`
}

/**
 * DESIGN.md §3's semantic colours, applied to a port.
 *
 * Only a port that is meant to be up and is not gets the red family — the same judgement the alert
 * rule makes, because a table where a deliberately shut port is red and raises no alert teaches an
 * operator to ignore red.
 */
export function interfaceTone(link: Pick<DeviceInterface, 'operStatus' | 'adminStatus'>): string {
  if (isShut(link)) return 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400'
  switch (link.operStatus) {
    case 'Up':
      return 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-300'
    case 'Down':
    case 'LowerLayerDown':
      return 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-300'
    case 'Dormant':
    case 'Testing':
      return 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300'
    default:
      return 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400'
  }
}

/** Whether somebody has shut this port on the device. Not a fault, and the reason it is not red. */
export function isShut(link: Pick<DeviceInterface, 'adminStatus'>): boolean {
  return link.adminStatus === 'Down' || link.adminStatus === 'Testing'
}

const STATUS_LABELS: Record<InterfaceStatus, string> = {
  Up: 'Up',
  Down: 'Down',
  Testing: 'Testing',
  Dormant: 'Dormant',
  NotPresent: 'Not present',
  LowerLayerDown: 'Lower layer down',
  // The MIB's own "unknown" — the agent cannot tell — against a value it never sent at all.
  NotReported: 'Unknown to the device',
  Unknown: 'Not reported',
}

export function interfaceStatusLabel(link: Pick<DeviceInterface, 'operStatus' | 'adminStatus'>): string {
  return isShut(link) ? 'Shut' : STATUS_LABELS[link.operStatus]
}

/** The bar's width. Clamped where the number is not: a wrong ifSpeed still reads as a full bar. */
export function utilisationBarWidth(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '0%'
  return `${Math.min(Math.max(value, 0), 100)}%`
}
