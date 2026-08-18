import { describe, expect, it } from 'vitest'
import { describeMinutes, describePolicyConditions, type SlaPolicy } from '../../api/sla'

function policy(overrides: Partial<SlaPolicy> = {}): SlaPolicy {
  return {
    id: 'p1', name: 'Policy', sortOrder: 0,
    priority: null, ticketType: null, categoryId: null, categoryName: null,
    responseTargetMinutes: 30, resolutionTargetMinutes: 480, warningPercent: 80,
    calendarId: 'c1', calendarName: 'Business hours', isActive: true, ticketCount: 0,
    ...overrides,
  }
}

describe('describePolicyConditions', () => {
  /** A policy stating no condition is the catch-all, and has to read as one rather than as blank. */
  it('says "any" for the conditions a policy leaves open', () => {
    expect(describePolicyConditions(policy())).toBe('Any priority · Any kind')
  })

  it('names each condition that is set', () => {
    expect(describePolicyConditions(policy({
      priority: 'Critical', ticketType: 'Incident', categoryName: 'Network',
    }))).toBe('Critical · Incidents · Network')
  })

  it('reads service requests as a kind rather than as an enum value', () => {
    expect(describePolicyConditions(policy({ ticketType: 'ServiceRequest' })))
      .toBe('Any priority · Service requests')
  })
})

describe('describeMinutes', () => {
  /** Targets are entered in minutes because that is what the server stores, but nobody reads 2880. */
  it('reads minutes as hours and days where they divide evenly', () => {
    expect(describeMinutes(30)).toBe('30m')
    expect(describeMinutes(60)).toBe('1h')
    expect(describeMinutes(480)).toBe('8h')
    expect(describeMinutes(1440)).toBe('1d')
    expect(describeMinutes(2880)).toBe('2d')
  })

  it('keeps the remainder when they do not', () => {
    expect(describeMinutes(90)).toBe('1h 30m')
    expect(describeMinutes(1500)).toBe('25h')
  })
})
