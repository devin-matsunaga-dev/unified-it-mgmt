import { describe, expect, it } from 'vitest'
import { builtInView, builtInViewIds, builtInViews } from './ticketViews'
import { normalizeFilter } from './ticketFilters'

describe('builtInViews', () => {
  it('ships five defaults with unique ids and a reason each', () => {
    expect(builtInViews).toHaveLength(5)
    expect(new Set(builtInViewIds).size).toBe(builtInViewIds.length)
    for (const view of builtInViews) {
      expect(view.label.length).toBeGreaterThan(0)
      expect(view.description.length).toBeGreaterThan(0)
    }
  })

  /** All tickets has always meant "no narrowing", and the chip is only active when that is true. */
  it('gives All tickets an empty filter', () => {
    expect(builtInView('all')!.filter('technician1')).toEqual({})
    expect(normalizeFilter(builtInView('all')!.filter('technician1'))).toEqual({})
  })

  /**
   * Every default's filter has to survive normalisation unchanged, or the chip would never read as
   * active: the list compares the normalised filter against the view's own.
   */
  it('defines every filter in its normalised form', () => {
    for (const view of builtInViews) {
      const filter = view.filter('technician1')
      expect(normalizeFilter(filter)).toEqual(filter)
    }
  })

  it('narrows on something in every view but All tickets', () => {
    for (const view of builtInViews) {
      if (view.id === 'all') continue
      expect(Object.keys(view.filter('technician1')).length).toBeGreaterThan(0)
    }
  })

  it('returns nothing for an id it does not know', () => {
    expect(builtInView('nope')).toBeUndefined()
  })
})
