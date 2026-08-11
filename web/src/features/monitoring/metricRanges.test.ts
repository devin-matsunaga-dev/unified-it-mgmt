import { describe, expect, it } from 'vitest'
import { axisFormatter, metricRanges, parseSeriesKey, resolutionLabel, seriesKey, windowFor } from './metricRanges'

describe('metric ranges', () => {
  /**
   * WP-3.4 refuses raw resolution over more than a day (a truncated chart must never look like a
   * complete one), so the picker must never offer a range/resolution pair the server would reject.
   */
  it('never asks for raw resolution over more than 24 hours', () => {
    for (const range of metricRanges) {
      if (range.resolution === 'Raw') expect(range.seconds).toBeLessThanOrEqual(86_400)
    }
  })

  it('anchors a range to now and reaches back by its own length', () => {
    const now = new Date('2026-08-11T12:00:00.000Z')
    const window = windowFor('24h', now)

    expect(window.to).toBe('2026-08-11T12:00:00.000Z')
    expect(window.from).toBe('2026-08-10T12:00:00.000Z')
    expect(window.resolution).toBe('Auto')
  })

  it('falls back to 24 hours when asked for a range that does not exist', () => {
    const window = windowFor('nonsense' as never, new Date('2026-08-11T12:00:00.000Z'))
    expect(window.from).toBe('2026-08-10T12:00:00.000Z')
  })

  /**
   * A metric name is not a series — a metric name plus a check is (the WP-3.4 defect). The key has
   * to survive a metric name that itself contains a colon.
   */
  it('round-trips a series key even when the metric name contains a colon', () => {
    const key = seriesKey('check:latency:ms', 'check-1')
    expect(parseSeriesKey(key)).toEqual({ metric: 'check:latency:ms', checkId: 'check-1' })
  })

  it('reads nothing out of a key that is not one', () => {
    expect(parseSeriesKey('no-separator-here')).toBeNull()
  })

  /** A 30-day chart labelled "14:05" says nothing about which day. */
  it('labels a long range by date and a short one by clock', () => {
    const point = '2026-08-11T14:05:00.000Z'
    expect(axisFormatter('1h')(point)).toMatch(/\d{2}[:.]\d{2}/)
    expect(axisFormatter('30d')(point)).not.toMatch(/\d{2}[:.]\d{2}/)
  })

  it('states the resolution that actually answered', () => {
    expect(resolutionLabel('Raw', 0)).toBe('every reading')
    expect(resolutionLabel('FiveMinute', 300)).toBe('5-minute averages')
  })
})
