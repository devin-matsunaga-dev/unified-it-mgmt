import { describe, expect, it } from 'vitest'
import type { Alert } from '../../api/monitoring'
import { correlationByCi, openAlertsByCi, tierFor, tierStyles } from './nodeEmphasis'

function alert(overrides: Partial<Alert>): Alert {
  return {
    id: 'alert-1', deviceId: 'device-1', ciId: 'ci-1', checkId: 'check-1', ruleId: 'rule-1',
    metricName: 'icmp.reachable', severity: 'Critical', status: 'Open', summary: 'Down',
    lastValue: 0, threshold: 1, consecutiveBreaches: 3, isFlapping: false,
    suppression: 'None', rootCauseAlertId: null, impactedCount: 0,
    raisedAt: '2026-08-18T00:00:00Z', lastObservedAt: '2026-08-18T00:05:00Z', clearedAt: null,
    pollerName: 'poller-1', acknowledgedAt: null, acknowledgedBy: null, acknowledgedByName: null,
    deviceAddress: '10.10.0.1', checkName: 'Ping', ciFound: true, ciName: 'core', ciType: 'NetworkDevice',
    ...overrides,
  } as Alert
}

describe('tierFor', () => {
  /** §3: the roles that carry the estate get the most weight. */
  it('puts edge, firewall and core devices at the top tier', () => {
    for (const networkRole of ['Edge', 'Firewall', 'Core']) {
      expect(tierFor({ type: 'NetworkDevice', networkRole })).toBe('primary')
    }
  })

  it('puts access, distribution and wireless devices below them', () => {
    for (const networkRole of ['Distribution', 'Access', 'Wireless']) {
      expect(tierFor({ type: 'NetworkDevice', networkRole })).toBe('secondary')
    }
  })

  /** Guessing downward would bury a core switch nobody has classified yet. */
  it('treats an unclassified network device as infrastructure, not as normal', () => {
    expect(tierFor({ type: 'NetworkDevice', networkRole: null })).toBe('secondary')
  })

  it('ranks servers above VMs, applications and services', () => {
    expect(tierFor({ type: 'Server', networkRole: null })).toBe('secondary')
    for (const type of ['Virtual', 'Software', 'Logical'] as const) {
      expect(tierFor({ type, networkRole: null })).toBe('normal')
    }
  })

  it('puts endpoint hardware at the lowest tier', () => {
    expect(tierFor({ type: 'Hardware', networkRole: null })).toBe('low')
  })
})

describe('tierStyles', () => {
  /** Weight descends with the tier, and every card stays inside the layout's row pitch. */
  it('descends in height and never exceeds the row pitch', () => {
    expect(tierStyles.primary.height).toBeGreaterThan(tierStyles.secondary.height)
    expect(tierStyles.secondary.height).toBeGreaterThan(tierStyles.normal.height)
    expect(tierStyles.normal.height).toBeGreaterThan(tierStyles.low.height)
    // layout.ts: nodeHeight 76 + gapY 104.
    for (const style of Object.values(tierStyles)) expect(style.height).toBeLessThan(180)
  })
})

describe('correlationByCi', () => {
  /** §13: the cause has to be distinguishable from what it took out. */
  it('separates the root cause from the CIs suppressed under it', () => {
    const roles = correlationByCi([
      alert({ id: 'a', ciId: 'switch', impactedCount: 3 }),
      alert({ id: 'b', ciId: 'server-a', suppression: 'RootCause', rootCauseAlertId: 'a' }),
      alert({ id: 'c', ciId: 'server-b', suppression: 'RootCause', rootCauseAlertId: 'a' }),
    ])

    expect(roles.get('switch')).toBe('RootCause')
    expect(roles.get('server-a')).toBe('Affected')
    expect(roles.get('server-b')).toBe('Affected')
  })

  /** A CI explaining others is the one worth opening, even if its own alert is suppressed too. */
  it('reports a CI that is both as the cause', () => {
    const roles = correlationByCi([
      alert({ ciId: 'switch', suppression: 'RootCause', impactedCount: 2 }),
    ])

    expect(roles.get('switch')).toBe('RootCause')
  })

  it('says nothing about a CI whose alert stands alone', () => {
    expect(correlationByCi([alert({ ciId: 'lonely' })]).has('lonely')).toBe(false)
  })

  /** Maintenance and flapping are suppressions too, and neither means "something else caused this". */
  it('ignores suppressions that are not a root cause', () => {
    const roles = correlationByCi([
      alert({ ciId: 'quiet', suppression: 'Maintenance' }),
      alert({ ciId: 'noisy', suppression: 'Flapping' }),
    ])

    expect(roles.size).toBe(0)
  })
})

describe('openAlertsByCi', () => {
  it('counts open alerts per CI and ignores cleared ones', () => {
    const counts = openAlertsByCi([
      alert({ id: 'a', ciId: 'switch' }),
      alert({ id: 'b', ciId: 'switch' }),
      alert({ id: 'c', ciId: 'switch', clearedAt: '2026-08-18T01:00:00Z' }),
    ])

    expect(counts.get('switch')).toBe(2)
  })
})
