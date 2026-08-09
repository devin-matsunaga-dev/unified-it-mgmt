import { describe, expect, it } from 'vitest'
import type { CiRelationship } from '../../api/assets'
import { ciRelationshipVerb, describeRelationship, relationshipCounterpart } from './relationships'

const edge: CiRelationship = {
  id: 'edge-1',
  sourceCiId: 'ci-vm', sourceCiName: 'vm-payroll', sourceCiType: 'Virtual',
  targetCiId: 'ci-host', targetCiName: 'esx-01', targetCiType: 'Server',
  type: 'RunsOn', description: null, createdBy: 'technician1', createdAt: '2026-08-07T09:00:00Z',
}

describe('ciRelationshipVerb', () => {
  it('turns each type into the verb that makes an edge a sentence', () => {
    expect(ciRelationshipVerb('RunsOn')).toBe('runs on')
    expect(ciRelationshipVerb('HostedOn')).toBe('is hosted on')
  })

  // The card renders whatever the API returns; a type this build has not heard of must not blank the row.
  it('falls back to the raw value for an unknown type', () => {
    expect(ciRelationshipVerb('BackedUpBy')).toBe('BackedUpBy')
  })
})

describe('relationshipCounterpart', () => {
  it('reads the target as upstream when the open CI is the source', () => {
    expect(relationshipCounterpart(edge, 'ci-vm'))
      .toEqual({ direction: 'Upstream', ciId: 'ci-host', name: 'esx-01', type: 'Server' })
  })

  it('reads the source as downstream when the open CI is the target', () => {
    expect(relationshipCounterpart(edge, 'ci-host'))
      .toEqual({ direction: 'Downstream', ciId: 'ci-vm', name: 'vm-payroll', type: 'Virtual' })
  })
})

describe('describeRelationship', () => {
  it('always reads source first, so the words agree with WP-2.3\'s direction convention', () => {
    expect(describeRelationship(edge)).toBe('vm-payroll runs on esx-01')
  })
})
