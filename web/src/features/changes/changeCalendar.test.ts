import { expect, test } from 'vitest'
import type { Change } from '../../api/changes'
import type { MaintenanceWindow } from '../../api/monitoring'
import {
  addMonths,
  formatPeriod,
  fromLocalInput,
  monthGrid,
  monthRange,
  placeEntries,
  toLocalInput,
} from './changeCalendar'

const change = (over: Partial<Change> = {}): Change => ({
  id: 'chg-1',
  number: 'CHG-000001',
  title: 'Firmware upgrade',
  description: 'The switch reboots twice.',
  status: 'Submitted',
  plannedStartAt: '2026-08-12T09:00:00Z',
  plannedEndAt: '2026-08-12T11:00:00Z',
  includeDependents: false,
  requestedById: 'technician1',
  requestedByName: 'Technician One',
  requestedAt: '2026-08-10T09:00:00Z',
  decidedById: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  updatedAt: '2026-08-10T09:00:00Z',
  ciCount: 1,
  dependentCount: 0,
  nextStatuses: ['Approved', 'Rejected', 'Draft', 'Cancelled'],
  cis: null,
  ...over,
})

const window = (over: Partial<MaintenanceWindow> = {}): MaintenanceWindow => ({
  id: 'win-1',
  name: 'CHG-000001 — Firmware upgrade',
  description: null,
  startsAt: '2026-08-12T09:00:00Z',
  endsAt: '2026-08-12T11:00:00Z',
  appliesToAllDevices: false,
  deviceIds: ['dev-1'],
  isActive: true,
  status: 'Scheduled',
  createdBy: 'system:change-approval',
  createdAt: '2026-08-11T09:00:00Z',
  updatedBy: 'system:change-approval',
  updatedAt: '2026-08-11T09:00:00Z',
  changeRequestId: 'chg-1',
  ...over,
})

/** Local, because a month is a thing on somebody's wall — DESIGN §10's user-local rule. */
test('a month range runs from its first local instant to the first of the next', () => {
  const { from, to } = monthRange(new Date(2026, 7, 1))

  expect(from.getFullYear()).toBe(2026)
  expect(from.getMonth()).toBe(7)
  expect(from.getDate()).toBe(1)
  expect(to.getMonth()).toBe(8)
  expect(to.getDate()).toBe(1)
})

test('moving months rolls the year over in both directions', () => {
  expect(addMonths(new Date(2026, 11, 1), 1).getFullYear()).toBe(2027)
  expect(addMonths(new Date(2026, 0, 1), -1).getMonth()).toBe(11)
})

test('the grid is whole Monday-first weeks with the padding days marked out of month', () => {
  // 1 August 2026 is a Saturday, so the first row carries five days of July.
  const days = monthGrid(new Date(2026, 7, 1), new Date(2026, 7, 17))

  expect(days.length % 7).toBe(0)
  expect(days[0].date.getDay()).toBe(1)
  expect(days[0].inMonth).toBe(false)
  expect(days[5].date.getDate()).toBe(1)
  expect(days[5].inMonth).toBe(true)
  expect(days.filter((day) => day.inMonth)).toHaveLength(31)
})

test('today is marked once and only in the month it falls in', () => {
  const days = monthGrid(new Date(2026, 7, 1), new Date(2026, 7, 17, 14, 30))

  const today = days.filter((day) => day.isToday)
  expect(today).toHaveLength(1)
  expect(today[0].date.getDate()).toBe(17)
})

/**
 * The rule the whole calendar exists for: a change that spans days appears on every one of them. Drawn
 * only on its start date, the other days would look free.
 */
test('a multi-day change is drawn on every day it spans', () => {
  const days = placeEntries(
    monthGrid(new Date(2026, 7, 1), new Date(2026, 7, 1)),
    // Built from local parts rather than a Z literal: the grid is local, so a UTC instant would land on
    // a different day either side of Greenwich and make this test pass or fail on the runner's timezone.
    [change({
      plannedStartAt: new Date(2026, 7, 12, 22, 0).toISOString(),
      plannedEndAt: new Date(2026, 7, 14, 6, 0).toISOString(),
    })],
    [])

  const covered = days.filter((day) => day.entries.length > 0).map((day) => day.date.getDate())
  expect(covered).toContain(12)
  expect(covered).toContain(13)
  expect(covered).toContain(14)
  expect(covered).not.toContain(11)
  expect(covered).not.toContain(15)
})

test('a change and the window it opened are both drawn, the change first', () => {
  const days = placeEntries(
    monthGrid(new Date(2026, 7, 1), new Date(2026, 7, 1)), [change()], [window()])

  const day = days.find((entry) => entry.date.getDate() === 12 && entry.inMonth)!
  expect(day.entries.map((entry) => entry.kind)).toEqual(['change', 'window'])
})

test('a window that ends exactly at midnight belongs to the day it ran in, not the next one', () => {
  const days = placeEntries(
    monthGrid(new Date(2026, 7, 1), new Date(2026, 7, 1)),
    [],
    [window({
      startsAt: new Date(2026, 7, 12, 22, 0).toISOString(),
      endsAt: new Date(2026, 7, 13, 0, 0).toISOString(),
    })])

  expect(days.find((day) => day.date.getDate() === 12 && day.inMonth)!.entries).toHaveLength(1)
  expect(days.find((day) => day.date.getDate() === 13 && day.inMonth)!.entries).toHaveLength(0)
})

/**
 * The server already answered for an overlapping range, so anything outside the grid is a row that
 * arrived for some other reason. Clamping it onto the edge would be a claim nobody made.
 */
test('an entry outside the grid entirely is dropped rather than pinned to an edge', () => {
  const days = placeEntries(
    monthGrid(new Date(2026, 7, 1), new Date(2026, 7, 1)),
    [change({ plannedStartAt: '2025-03-01T09:00:00Z', plannedEndAt: '2025-03-01T10:00:00Z' })],
    [])

  expect(days.every((day) => day.entries.length === 0)).toBe(true)
})

test('a slot inside one day says the date once', () => {
  const rendered = formatPeriod(
    new Date(2026, 7, 12, 9, 0).toISOString(),
    new Date(2026, 7, 12, 11, 0).toISOString())

  expect(rendered.match(/Aug/g)).toHaveLength(1)
  expect(rendered).toContain('→')
})

test('a slot crossing midnight says both dates', () => {
  const rendered = formatPeriod(
    new Date(2026, 7, 12, 23, 0).toISOString(),
    new Date(2026, 7, 13, 2, 0).toISOString())

  expect(rendered.match(/Aug/g)).toHaveLength(2)
})

/** The browser's datetime-local field speaks local wall time; the API takes instants. */
test('a local datetime survives a round trip through the form field', () => {
  const local = new Date(2026, 7, 12, 9, 30)

  expect(toLocalInput(local)).toBe('2026-08-12T09:30')
  expect(new Date(fromLocalInput(toLocalInput(local))).getTime()).toBe(local.getTime())
})
