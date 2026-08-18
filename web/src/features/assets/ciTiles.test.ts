import { beforeEach, describe, expect, it } from 'vitest'
import type { CiFilter } from '../../api/assets'
import {
  builtInTiles, describeTileFilter, isTileApplied, narrowsAnything,
  readCustomTiles, toTileFilter, writeCustomTiles,
} from './ciTiles'

describe('toTileFilter', () => {
  /** A tile is a question about the estate, not a lookup somebody is doing or a page they are on. */
  it('keeps the narrowing and drops search and paging', () => {
    const filter: CiFilter = {
      type: 'Hardware', lifecycleState: 'Deployed', search: 'laptop', page: 3, pageSize: 50,
    }

    expect(toTileFilter(filter)).toEqual({ type: 'Hardware', lifecycleState: 'Deployed' })
  })

  it('drops empty members so two filters that mean the same thing compare equal', () => {
    expect(toTileFilter({ type: 'Hardware', customFields: [] })).toEqual({ type: 'Hardware' })
  })

  it('keeps a false isActive, which is a real constraint', () => {
    expect(toTileFilter({ isActive: false })).toEqual({ isActive: false })
  })
})

describe('narrowsAnything', () => {
  it('is false for a filter that narrows nothing, even with a search term', () => {
    expect(narrowsAnything({})).toBe(false)
    expect(narrowsAnything({ search: 'laptop', page: 2 })).toBe(false)
  })

  it('is true once anything is constrained', () => {
    expect(narrowsAnything({ lifecycleState: 'Retired' })).toBe(true)
  })
})

describe('isTileApplied', () => {
  it('matches when the list is narrowed to exactly what the tile counts', () => {
    expect(isTileApplied({ lifecycleState: 'Deployed' }, { lifecycleState: 'Deployed' })).toBe(true)
  })

  /** Search is not part of a tile, so typing in the box must not un-apply one. */
  it('ignores the search term', () => {
    expect(isTileApplied({ lifecycleState: 'Deployed', search: 'sw' }, { lifecycleState: 'Deployed' })).toBe(true)
  })

  /**
   * The previous version compared lifecycle and warranty only, so a tile carrying a type or an owner
   * read as applied while the list showed something else entirely.
   */
  it('does not match when the list carries a constraint the tile does not', () => {
    expect(isTileApplied({ lifecycleState: 'Deployed', type: 'Hardware' }, { lifecycleState: 'Deployed' }))
      .toBe(false)
  })

  it('does not match when the tile carries a constraint the list does not', () => {
    expect(isTileApplied({ lifecycleState: 'Deployed' }, { lifecycleState: 'Deployed', type: 'Hardware' }))
      .toBe(false)
  })

  it('compares custom field constraints by value', () => {
    const tile: CiFilter = { customFields: [{ fieldId: 'f1', value: 'Laptop' }] }
    expect(isTileApplied({ customFields: [{ fieldId: 'f1', value: 'Laptop' }] }, tile)).toBe(true)
    expect(isTileApplied({ customFields: [{ fieldId: 'f1', value: 'Printer' }] }, tile)).toBe(false)
  })

  /** The total tile is the one that counts everything, and it applies when nothing is narrowed. */
  it('treats the built-in total as applied only when nothing narrows the list', () => {
    const total = builtInTiles[0].filter
    expect(isTileApplied({}, total)).toBe(true)
    expect(isTileApplied({ search: 'anything' }, total)).toBe(true)
    expect(isTileApplied({ type: 'Server' }, total)).toBe(false)
  })
})

describe('describeTileFilter', () => {
  it('reads back what a tile counts', () => {
    expect(describeTileFilter({ type: 'Hardware', lifecycleState: 'Deployed' }))
      .toBe('Hardware · Deployed')
    expect(describeTileFilter({ customFields: [{ fieldId: 'f1', value: 'Laptop' }] })).toBe('Laptop')
  })

  it('says so when a filter narrows nothing', () => {
    expect(describeTileFilter({ search: 'ignored' })).toBe('Everything')
  })
})

describe('custom tile storage', () => {
  beforeEach(() => localStorage.clear())

  it('round-trips through storage', () => {
    const tiles = [{ id: 't1', label: 'Laptops out of warranty', filter: { type: 'Hardware' as const } }]
    writeCustomTiles('assets:tiles', tiles)

    expect(readCustomTiles('assets:tiles')).toEqual(tiles)
  })

  it('returns nothing when what is stored cannot be read', () => {
    localStorage.setItem('assets:tiles', '{ not json')
    expect(readCustomTiles('assets:tiles')).toEqual([])
  })

  /** This is user-editable storage, so one bad row must not cost the whole set. */
  it('keeps the rows that are well formed and drops the rest', () => {
    localStorage.setItem('assets:tiles', JSON.stringify([
      { id: 't1', label: 'Good', filter: {} },
      { id: 't2', label: 42, filter: {} },
      'nonsense',
      { id: 't3', filter: {} },
    ]))

    expect(readCustomTiles('assets:tiles').map((tile) => tile.id)).toEqual(['t1'])
  })
})
