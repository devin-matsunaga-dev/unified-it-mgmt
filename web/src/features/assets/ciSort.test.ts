import { describe, expect, it } from 'vitest'
import type { Ci } from '../../api/assets'
import { nextCiSort, sortCis, ciSortDescription } from './ciSort'

const base: Ci = {
  id: 'ci-1', type: 'Server', name: 'app-01', assetTag: 'AT-0001', serialNumber: 'SN-0001', description: null,
  isActive: true, lifecycleState: 'InStock',
  ownership: { ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteId: null, siteName: null, assignedAt: null },
  coverage: { contractId: null, contractName: null, contractNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: {}, customFields: [], createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T01:00:00Z',
}

const ci = (over: Partial<Ci> & { id: string }): Ci => ({ ...base, ...over })
const names = (items: Ci[]) => items.map((item) => item.name)

describe('nextCiSort', () => {
  it('cycles a column ascending, descending, then back to the server\'s own order', () => {
    const first = nextCiSort(null, 'name')
    expect(first).toEqual({ column: 'name', desc: false })
    const second = nextCiSort(first, 'name')
    expect(second).toEqual({ column: 'name', desc: true })
    expect(nextCiSort(second, 'name')).toBeNull()
  })

  it('starts a different column ascending rather than inheriting the last direction', () => {
    expect(nextCiSort({ column: 'name', desc: true }, 'type')).toEqual({ column: 'type', desc: false })
  })
})

describe('sortCis', () => {
  const items = [
    ci({ id: '1', name: 'switch-01', serialNumber: 'SN-3' }),
    ci({ id: '2', name: 'app-01', serialNumber: 'SN-1' }),
    ci({ id: '3', name: 'db-01', serialNumber: 'SN-2' }),
  ]

  it('leaves the server order alone when nothing is sorted', () => {
    expect(sortCis(items, null)).toBe(items)
  })

  it('sorts a text column both ways without mutating the source', () => {
    expect(names(sortCis(items, { column: 'name', desc: false }))).toEqual(['app-01', 'db-01', 'switch-01'])
    expect(names(sortCis(items, { column: 'name', desc: true }))).toEqual(['switch-01', 'db-01', 'app-01'])
    expect(names(items)).toEqual(['switch-01', 'app-01', 'db-01'])
  })

  // "Deployed, Disposed, In repair, In stock" is an ordering of spellings, not of asset lives.
  it('sorts lifecycle by its place in the state graph, not alphabetically', () => {
    const estate = [
      ci({ id: '1', name: 'disposed', lifecycleState: 'Disposed' }),
      ci({ id: '2', name: 'deployed', lifecycleState: 'Deployed' }),
      ci({ id: '3', name: 'ordered', lifecycleState: 'Ordered' }),
      ci({ id: '4', name: 'in-repair', lifecycleState: 'InRepair' }),
    ]

    expect(names(sortCis(estate, { column: 'lifecycleState', desc: false })))
      .toEqual(['ordered', 'deployed', 'in-repair', 'disposed'])
  })

  // A CI with no serial is missing a value, not holding the smallest one — burying it at the top of a
  // descending sort hides exactly the rows the operator asked to see.
  it('keeps blank cells last in both directions', () => {
    const sparse = [
      ci({ id: '1', name: 'blank', assetTag: null }),
      ci({ id: '2', name: 'tagged', assetTag: 'AT-2' }),
      ci({ id: '3', name: 'empty', assetTag: '' }),
    ]

    expect(names(sortCis(sparse, { column: 'assetTag', desc: false }))[0]).toBe('tagged')
    expect(names(sortCis(sparse, { column: 'assetTag', desc: true }))[0]).toBe('tagged')
  })

  it('falls back to name so an equal pair keeps a stable order', () => {
    const tied = [
      ci({ id: '1', name: 'zeta', type: 'Server' }),
      ci({ id: '2', name: 'alpha', type: 'Server' }),
    ]

    expect(names(sortCis(tied, { column: 'type', desc: false }))).toEqual(['alpha', 'zeta'])
    expect(names(sortCis(tied, { column: 'type', desc: true }))).toEqual(['alpha', 'zeta'])
  })

  it('sorts owner by the department when nobody holds the asset', () => {
    const owned = [
      ci({ id: '1', name: 'unowned', ownership: { ...base.ownership, departmentName: 'Zoology' } }),
      ci({ id: '2', name: 'owned', ownership: { ...base.ownership, ownerName: 'Ann Adams' } }),
    ]

    expect(names(sortCis(owned, { column: 'owner', desc: false }))).toEqual(['owned', 'unowned'])
  })
})

describe('ciSortDescription', () => {
  it('states the direction of the sorted column only', () => {
    expect(ciSortDescription({ column: 'name', desc: false }, 'name')).toBe('ascending')
    expect(ciSortDescription({ column: 'name', desc: true }, 'name')).toBe('descending')
    expect(ciSortDescription({ column: 'name', desc: true }, 'type')).toBe('none')
    expect(ciSortDescription(null, 'name')).toBe('none')
  })
})
