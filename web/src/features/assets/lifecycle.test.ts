import { describe, expect, it } from 'vitest'
import type { CiAssignmentEntry, CiLifecycleStateInfo } from '../../api/assets'
import { allowedTargets, ciLifecycleLabel, describeAssignment } from './lifecycle'

const states: CiLifecycleStateInfo[] = [
  { state: 'Ordered', allowedTargets: ['InStock'] },
  { state: 'Retired', allowedTargets: ['Disposed'] },
  { state: 'Disposed', allowedTargets: [] },
]

const entry = (overrides: Partial<CiAssignmentEntry>): CiAssignmentEntry => ({
  id: 'entry-1', ciId: 'ci-1', action: 'CheckOut',
  fromOwnerUserId: null, fromOwnerName: null, toOwnerUserId: null, toOwnerName: null,
  departmentId: null, departmentName: null, siteId: null, siteName: null,
  note: null, actorId: 'technician1', occurredAt: '2026-08-07T09:00:00Z', ...overrides,
})

describe('allowedTargets', () => {
  it('returns the server\'s targets for a known state', () => {
    expect(allowedTargets(states, 'Ordered')).toEqual(['InStock'])
  })

  it('returns nothing for a terminal state', () => {
    expect(allowedTargets(states, 'Disposed')).toEqual([])
  })

  // The graph is loaded asynchronously, so an unanswered query must not render a stale set of moves.
  it('returns nothing for a state the graph has not been loaded for', () => {
    expect(allowedTargets([], 'InStock')).toEqual([])
  })
})

describe('ciLifecycleLabel', () => {
  it('splits the compound states into sentence case', () => {
    expect(ciLifecycleLabel('InStock')).toBe('In stock')
    expect(ciLifecycleLabel('InRepair')).toBe('In repair')
  })
})

describe('describeAssignment', () => {
  it('names the new holder and where the CI went on a check-out', () => {
    expect(describeAssignment(entry({ toOwnerName: 'End User One', departmentName: 'Finance', siteName: 'Head Office' })))
      .toBe('End User One took it out (Finance · Head Office)')
  })

  it('names the previous holder on a check-in', () => {
    expect(describeAssignment(entry({ action: 'CheckIn', fromOwnerName: 'End User One', siteName: 'Head Office' })))
      .toBe('End User One returned it to Head Office')
  })

  it('names both people on a transfer', () => {
    expect(describeAssignment(entry({ action: 'Transfer', fromOwnerName: 'End User One', toOwnerName: 'End User Two' })))
      .toBe('End User One handed it to End User Two')
  })

  it('falls back to the placement when only the department or site moved', () => {
    expect(describeAssignment(entry({ action: 'Relocate', departmentName: 'Operations' }))).toBe('Moved to Operations')
  })
})
