import { describe, expect, it } from 'vitest'
import { formatDateOnly } from './utils'

describe('formatDateOnly', () => {
  it('states a calendar date as the locale writes it', () => {
    expect(formatDateOnly('2026-09-14')).toBe(
      new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(2026, 8, 14)))
  })

  // The reason this helper exists: `new Date('2026-09-14')` is UTC midnight, which is still the 13th
  // in every timezone west of Greenwich, so a warranty would appear to end a day early.
  it('keeps the day the API sent, not the day UTC midnight lands on locally', () => {
    expect(formatDateOnly('2026-09-14')).toContain('14')
    expect(formatDateOnly('2026-01-01')).toContain('1')
  })

  // Failure path: the value is whatever the API put in the field, and an unrecognised one must still
  // render rather than come out as "Invalid Date".
  it('returns anything that is not a calendar date untouched', () => {
    expect(formatDateOnly('')).toBe('')
    expect(formatDateOnly('2026-09-14T00:00:00Z')).toBe('2026-09-14T00:00:00Z')
    expect(formatDateOnly('not a date')).toBe('not a date')
  })
})
