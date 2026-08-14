import { describe, expect, it } from 'vitest'
import type { ImpactedCi } from '../../api/assets'
import { describeExposure, describeRadius, describeRing, exposureBadge, formatWindow, groupByDepth } from './blastRadius'

describe('exposureBadge', () => {
  it('leads with the breach, because a missed deadline is what changes the next decision', () => {
    expect(exposureBadge({ policyName: 'Standard', resolutionDueAt: '2026-08-14T12:00:00Z', remainingSeconds: 0, breached: true, atRisk: false }))
      .toEqual({ label: 'SLA breached', tone: 'critical' })
  })

  it('says how long is left on a ticket that is close to the line', () => {
    expect(exposureBadge({ policyName: 'Standard', resolutionDueAt: '2026-08-14T14:00:00Z', remainingSeconds: 7200, breached: false, atRisk: true }))
      .toEqual({ label: 'At risk · 2h left', tone: 'warning' })
  })

  /**
   * The distinction the panel exists to draw: a ticket with no policy has *no clock*, which must not
   * render as a ticket with plenty of time left.
   */
  it('says a ticket with no policy has no clock rather than implying it has time', () => {
    expect(exposureBadge(null)).toEqual({ label: 'No SLA', tone: 'neutral' })
  })
})

describe('formatWindow', () => {
  it.each([
    [90, '1m'],
    [7200, '2h'],
    [172800, '2d'],
  ])('renders %i seconds as %s', (seconds, expected) => {
    expect(formatWindow(seconds)).toBe(expected)
  })

  /** A negative remainder is a breach, which the badge above words as one; here it must not read as time. */
  it('never renders a negative remainder as time in hand', () => {
    expect(formatWindow(-3600)).toBe('0m')
  })
})

describe('describeRadius', () => {
  /**
   * A blast radius of one is a real answer. Wording it as "0 affected" would read as a broken panel on
   * every CI nothing depends on, which is most laptops in the estate.
   */
  it('says so plainly when nothing else depends on the CI', () => {
    expect(describeRadius({ ciCount: 1, directCiCount: 0 })).toBe('Nothing else recorded depends on this')
  })

  it('counts the others and how many of them fail immediately', () => {
    expect(describeRadius({ ciCount: 9, directCiCount: 3 })).toBe('8 other CIs affected · 3 directly')
  })

  it('says "CI" rather than "CIs" when exactly one other is affected', () => {
    expect(describeRadius({ ciCount: 2, directCiCount: 1 })).toBe('1 other CI affected · 1 directly')
  })
})

describe('describeExposure', () => {
  it('has nothing to say when no ticket is open on the radius', () => {
    expect(describeExposure({ breachedSlaCount: 0, atRiskSlaCount: 0, openTicketCount: 0 })).toBeNull()
  })

  it('states the breaches and the near misses', () => {
    expect(describeExposure({ breachedSlaCount: 2, atRiskSlaCount: 1, openTicketCount: 5 }))
      .toBe('2 SLA breached · 1 at risk')
  })

  /** Open work with every clock in hand is worth saying: silence there reads as an unanswered question. */
  it('says there are no breaches when work is open and none of it is late', () => {
    expect(describeExposure({ breachedSlaCount: 0, atRiskSlaCount: 0, openTicketCount: 3 })).toBe('No SLA breaches')
  })
})

describe('groupByDepth', () => {
  const ci = (id: string, depth: number): ImpactedCi => ({
    ciId: id, name: id, type: 'Server', lifecycleState: 'Deployed', isActive: true, depth,
    ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteName: null,
    openTicketCount: 0,
  })

  it('puts the nearest ring first', () => {
    const rings = groupByDepth([ci('a', 0), ci('b', 1), ci('c', 1), ci('d', 2)])

    expect(rings.map((ring) => ring.depth)).toEqual([0, 1, 2])
    expect(rings[1].cis.map((entry) => entry.ciId)).toEqual(['b', 'c'])
  })

  /**
   * The API already orders by depth and then by name. Re-sorting here would let two renderings of one
   * answer disagree about the order, so the grouping preserves what it was given.
   */
  it('keeps the order the API gave within a ring', () => {
    const rings = groupByDepth([ci('zebra', 1), ci('apple', 1)])

    expect(rings[0].cis.map((entry) => entry.ciId)).toEqual(['zebra', 'apple'])
  })

  it('has no rings at all for an empty radius', () => {
    expect(groupByDepth([])).toEqual([])
  })
})

describe('describeRing', () => {
  it.each([
    [0, 'This CI'],
    [1, 'Directly dependent'],
    [3, '3 hops away'],
  ])('calls depth %i "%s"', (depth, expected) => {
    expect(describeRing(depth)).toBe(expected)
  })
})
