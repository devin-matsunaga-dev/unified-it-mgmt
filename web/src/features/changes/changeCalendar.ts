import type { Change } from '../../api/changes'
import type { MaintenanceWindow } from '../../api/monitoring'

/** One thing drawn on a day: a change somebody planned, or the window an approval opened. */
export type CalendarEntry =
  | { kind: 'change'; id: string; change: Change }
  | { kind: 'window'; id: string; window: MaintenanceWindow }

export type CalendarDay = {
  /** Local calendar date, midnight. */
  date: Date
  /** False for the leading and trailing days that pad the grid to whole weeks. */
  inMonth: boolean
  isToday: boolean
  entries: CalendarEntry[]
}

/**
 * The month a calendar is showing, as a half-open instant range.
 *
 * Half-open on purpose: the end is the first instant of the next month, so an entry starting at 23:59 on
 * the last day is inside the range and nothing has to reason about the last representable millisecond.
 * Both ends are local, because a month is a thing on somebody's wall and not a UTC construct — DESIGN §10
 * says every timestamp is shown in user-local time, and a calendar that disagreed with the clock beside it
 * would be worse than one that disagreed with the server.
 */
export function monthRange(month: Date) {
  const from = new Date(month.getFullYear(), month.getMonth(), 1)
  const to = new Date(month.getFullYear(), month.getMonth() + 1, 1)
  return { from, to }
}

export const addMonths = (month: Date, delta: number) =>
  new Date(month.getFullYear(), month.getMonth() + delta, 1)

export const startOfDay = (value: Date) =>
  new Date(value.getFullYear(), value.getMonth(), value.getDate())

/**
 * The days of the grid: whole weeks, Monday first, padded either side so every row has seven cells.
 *
 * Six rows are not forced. A month that fits in five weeks gets five, because a permanently empty
 * trailing row reads as a fortnight in which nothing is scheduled rather than as spacing.
 */
export function monthGrid(month: Date, today: Date): CalendarDay[] {
  const { from, to } = monthRange(month)
  // getDay() is Sunday-first; this app's weeks start on Monday.
  const leading = (from.getDay() + 6) % 7
  const trailing = (7 - (to.getDay() + 6) % 7) % 7

  const first = new Date(from.getFullYear(), from.getMonth(), from.getDate() - leading)
  const dayCount = leading + Math.round((to.getTime() - from.getTime()) / 86_400_000) + trailing

  const startOfToday = startOfDay(today).getTime()
  const days: CalendarDay[] = []
  for (let index = 0; index < dayCount; index++) {
    const date = new Date(first.getFullYear(), first.getMonth(), first.getDate() + index)
    days.push({
      date,
      inMonth: date.getMonth() === from.getMonth() && date.getFullYear() === from.getFullYear(),
      isToday: date.getTime() === startOfToday,
      entries: [],
    })
  }

  return days
}

/**
 * Places changes and windows onto the days their planned period covers.
 *
 * An entry appears on *every* day it spans, not only the day it starts. A three-day change that showed up
 * once would leave two days looking free, which is the one thing a maintenance calendar exists to prevent
 * — and it is why the grid is built from the period rather than from a start date.
 *
 * Entries whose period lies wholly outside the grid are dropped rather than clamped onto its edges: the
 * server already answered for an overlapping range, and a change from last March pinned to the first of
 * this month would be a claim nobody made.
 */
export function placeEntries(
  days: CalendarDay[],
  changes: readonly Change[],
  windows: readonly MaintenanceWindow[],
): CalendarDay[] {
  const placed = days.map((day) => ({ ...day, entries: [] as CalendarEntry[] }))
  if (placed.length === 0) return placed

  const spans: { entry: CalendarEntry; from: number; to: number }[] = [
    ...changes.map((change) => ({
      entry: { kind: 'change', id: change.id, change } as CalendarEntry,
      from: new Date(change.plannedStartAt).getTime(),
      to: new Date(change.plannedEndAt).getTime(),
    })),
    ...windows.map((window) => ({
      entry: { kind: 'window', id: window.id, window } as CalendarEntry,
      from: new Date(window.startsAt).getTime(),
      to: new Date(window.endsAt).getTime(),
    })),
  ]

  for (const day of placed) {
    const dayStart = day.date.getTime()
    const dayEnd = dayStart + 86_400_000
    for (const span of spans) {
      // Overlap, with the day half-open: a window ending exactly at midnight belongs to the day it ran
      // in and not to the one that had not started.
      if (span.from < dayEnd && span.to > dayStart) {
        day.entries.push(span.entry)
      }
    }

    day.entries.sort(compareEntries)
  }

  return placed
}

/** Changes above their windows, then by when each begins, then by name so the order never wobbles. */
function compareEntries(left: CalendarEntry, right: CalendarEntry) {
  if (left.kind !== right.kind) return left.kind === 'change' ? -1 : 1
  return entryStart(left) - entryStart(right) || entryLabel(left).localeCompare(entryLabel(right))
}

const entryStart = (entry: CalendarEntry) =>
  new Date(entry.kind === 'change' ? entry.change.plannedStartAt : entry.window.startsAt).getTime()

export const entryLabel = (entry: CalendarEntry) =>
  entry.kind === 'change' ? entry.change.title : entry.window.name

/**
 * "14 Aug 09:00 → 11:00" for a slot inside one day, "14 Aug 09:00 → 15 Aug 02:00" when it crosses one.
 * The date is repeated only when it changes, because a maintenance slot is usually an hour and repeating
 * the day makes the interesting half harder to read.
 */
export function formatPeriod(startsAt: string, endsAt: string) {
  const start = new Date(startsAt)
  const end = new Date(endsAt)
  const date = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' })
  const time = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })
  const sameDay = startOfDay(start).getTime() === startOfDay(end).getTime()
  return sameDay
    ? `${date.format(start)} ${time.format(start)} → ${time.format(end)}`
    : `${date.format(start)} ${time.format(start)} → ${date.format(end)} ${time.format(end)}`
}

/** The value an `<input type="datetime-local">` wants: local wall time with no zone and no seconds. */
export function toLocalInput(value: Date) {
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`
    + `T${pad(value.getHours())}:${pad(value.getMinutes())}`
}

/** And back again. The browser gives local wall time; the API takes instants. */
export const fromLocalInput = (value: string) => new Date(value).toISOString()
