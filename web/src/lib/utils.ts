import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

/**
 * A calendar date (`yyyy-MM-dd`) as the reader's locale writes it. WP-2.6 stores contract, purchase
 * and warranty dates as `DateOnly` precisely because an agreement ends on a day rather than at an
 * instant, so this must never go through `Date` parsing: `new Date('2026-09-14')` is UTC midnight,
 * which renders as the 13th anywhere west of Greenwich. The parts are read and re-stated locally.
 */
export function formatDateOnly(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (!match) return value
  const [, year, month, day] = match
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })
    .format(new Date(Number(year), Number(month) - 1, Number(day)))
}
