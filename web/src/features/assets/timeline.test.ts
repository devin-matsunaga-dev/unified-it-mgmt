import { describe, expect, it } from 'vitest'
import type { CiTimelineEntry, CiTimelineSource } from '../../api/assets'
import {
  describeTimeline,
  describeTruncation,
  groupByDay,
  timelineDayLabel,
  timelineDot,
  timelineKindLabel,
} from './timeline'

function entry(overrides: Partial<CiTimelineEntry> = {}): CiTimelineEntry {
  return {
    kind: 'Config',
    id: 'entry-1',
    occurredAt: '2026-08-14T10:00:00Z',
    title: 'Record updated',
    detail: null,
    actor: 'alex',
    severity: null,
    status: null,
    priority: null,
    alertId: null,
    deviceId: null,
    ticketId: null,
    ticketNumber: null,
    linkedAt: null,
    ...overrides,
  }
}

describe('timelineDot', () => {
  // DESIGN.md §3: a Critical is always the red family, wherever it is drawn.
  it('colours an alert by its severity and nothing else', () => {
    expect(timelineDot({ kind: 'Alert', severity: 'Critical' })).toContain('red')
    expect(timelineDot({ kind: 'Alert', severity: 'Warning' })).toContain('amber')
    expect(timelineDot({ kind: 'Alert', severity: 'Ok' })).toContain('green')
  })

  // A cleared alert is still an alert that happened; the row says it recovered.
  it('keeps an alert its severity colour after it has recovered', () => {
    expect(timelineDot({ kind: 'Alert', severity: 'Critical' })).toContain('red')
  })

  it('leaves everything that is not an alert neutral', () => {
    expect(timelineDot({ kind: 'Ticket', severity: null })).toContain('slate')
    expect(timelineDot({ kind: 'Lifecycle', severity: null })).toContain('slate')
    expect(timelineDot({ kind: 'Config', severity: null })).toContain('slate')
  })
})

describe('groupByDay', () => {
  it('groups consecutive entries of one day together and keeps the order it was given', () => {
    const days = groupByDay([
      entry({ id: 'a', occurredAt: '2026-08-14T10:00:00Z' }),
      entry({ id: 'b', occurredAt: '2026-08-14T08:00:00Z' }),
      entry({ id: 'c', occurredAt: '2026-08-12T23:00:00Z' }),
    ])

    expect(days).toHaveLength(2)
    expect(days[0].entries.map((item) => item.id)).toEqual(['a', 'b'])
    expect(days[1].entries.map((item) => item.id)).toEqual(['c'])
  })

  it('has nothing to group when the timeline is empty', () => {
    expect(groupByDay([])).toEqual([])
  })
})

describe('timelineDayLabel', () => {
  const now = new Date('2026-08-14T12:00:00Z')

  it('names today and yesterday rather than dating them', () => {
    expect(timelineDayLabel('2026-08-14T09:00:00Z', now)).toBe('Today')
    expect(timelineDayLabel('2026-08-13T09:00:00Z', now)).toBe('Yesterday')
  })

  it('dates anything older', () => {
    expect(timelineDayLabel('2026-07-02T09:00:00Z', now)).not.toBe('Today')
    expect(timelineDayLabel('2026-07-02T09:00:00Z', now)).toContain('2026')
  })
})

describe('describeTimeline', () => {
  it('states how many are on screen and how many there are', () => {
    expect(describeTimeline({ entryCount: 12, totalCount: 12, truncated: false }, false))
      .toBe('12 events, most recent first')
    expect(describeTimeline({ entryCount: 50, totalCount: 431, truncated: true }, false))
      .toBe('50 events of 431, most recent first')
  })

  it('counts one event without pluralising it', () => {
    expect(describeTimeline({ entryCount: 1, totalCount: 1, truncated: false }, false))
      .toBe('1 event, most recent first')
  })

  /**
   * The misreading this screen is most exposed to: an asset that has never alerted, viewed through the
   * alerts filter, must not read as an asset nothing has ever happened to.
   */
  it('says an empty answer is about the filter when a filter is on', () => {
    expect(describeTimeline({ entryCount: 0, totalCount: 0, truncated: false }, true))
      .toBe('Nothing of the kinds you selected')
    expect(describeTimeline({ entryCount: 0, totalCount: 0, truncated: false }, false))
      .toBe('Nothing has been recorded against this asset yet')
  })
})

describe('describeTruncation', () => {
  function source(overrides: Partial<CiTimelineSource> = {}): CiTimelineSource {
    return { kind: 'Alert', requested: true, returned: 50, total: 400, truncated: true, ...overrides }
  }

  it('names each source that ran out of room and what it is holding back', () => {
    expect(describeTruncation([source(), source({ kind: 'Ticket', returned: 3, total: 3, truncated: false })]))
      .toBe('alerts: showing 50 of 400')
  })

  it('says nothing at all when every source is whole', () => {
    expect(describeTruncation([source({ truncated: false })])).toBeNull()
  })
})

describe('timelineKindLabel', () => {
  it('names the four kinds in sentence case', () => {
    expect(timelineKindLabel('Alert')).toBe('Alerts')
    expect(timelineKindLabel('Config')).toBe('Configuration')
  })
})
