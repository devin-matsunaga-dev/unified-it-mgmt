import { describe, expect, it } from 'vitest'
import { correlationBadge, suppressionLabel } from './correlation'

describe('suppressionLabel', () => {
  it('says nothing about an alert that was not suppressed', () => {
    expect(suppressionLabel('None')).toBeNull()
  })

  it.each(['Maintenance', 'Flapping'] as const)('names the state an operator put the device in: %s', (kind) => {
    expect(suppressionLabel(kind)).toBe(`Suppressed: ${kind}`)
  })

  /**
   * Not "Suppressed: RootCause". The other two name a state somebody chose; this one names a judgement
   * the platform made, and it has to read as a sentence to be arguable with.
   */
  it('words root-cause suppression as a sentence rather than as an enum member', () => {
    expect(suppressionLabel('RootCause')).toBe('Suppressed under its root cause')
  })
})

describe('correlationBadge', () => {
  const alert = (overrides: Partial<Parameters<typeof correlationBadge>[0]> = {}) => ({
    suppression: 'None' as const,
    rootCauseAlertId: null,
    impactedCount: 0,
    ...overrides,
  })

  it('says nothing about an alert that explains nothing and is explained by nothing', () => {
    expect(correlationBadge(alert())).toBeNull()
  })

  /** The row somebody has to act on: it carries the size of the outage. */
  it('tells a root cause how much it is holding down', () => {
    expect(correlationBadge(alert({ impactedCount: 5 }))).toEqual({
      label: 'Root cause · 5 impacted',
      tone: 'info',
    })
  })

  it('tells a suppressed alert that it is held', () => {
    expect(correlationBadge(alert({
      suppression: 'RootCause',
      rootCauseAlertId: 'alert-cause',
    }))).toEqual({ label: 'Suppressed under its root cause', tone: 'neutral' })
  })

  /**
   * Filed under a cause but not suppressed by it: this alert had already been published when the cause
   * appeared, so it has a ticket of its own. "Related" rather than "suppressed" is the difference
   * between a ticket somebody should expect to find and one they should not.
   */
  it('distinguishes an alert that is merely related from one that was silenced', () => {
    expect(correlationBadge(alert({ rootCauseAlertId: 'alert-cause' }))).toEqual({
      label: 'Related to a root cause',
      tone: 'neutral',
    })
  })

  /** Being a cause is the more useful thing to say, if the two ever did coincide. */
  it('prefers the cause reading when an alert is somehow both', () => {
    expect(correlationBadge(alert({
      suppression: 'RootCause',
      rootCauseAlertId: 'alert-cause',
      impactedCount: 2,
    }))?.tone).toBe('info')
  })
})
