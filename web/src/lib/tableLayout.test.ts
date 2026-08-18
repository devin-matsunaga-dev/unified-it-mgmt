import { beforeEach, describe, expect, it } from 'vitest'
import {
  defaultLayout, isColumnVisible, moveColumn, readLayout, reconcileLayout,
  toggleColumn, visibleColumns, writeLayout,
} from './tableLayout'

const known = ['name', 'type', 'owner', 'site'] as const
type Id = (typeof known)[number]

describe('reconcileLayout', () => {
  it('starts from the defined order when nothing is stored', () => {
    expect(reconcileLayout(known, null)).toEqual({ order: ['name', 'type', 'owner', 'site'], hidden: [] })
  })

  /** A column removed from the app must not be resurrected by somebody's saved layout. */
  it('drops ids that no longer exist', () => {
    const layout = reconcileLayout(known, { order: ['owner', 'gone', 'name'] as unknown as Id[], hidden: ['gone'] as unknown as Id[] })

    expect(layout.order).not.toContain('gone')
    expect(layout.hidden).not.toContain('gone')
  })

  /** A new column has to appear for everybody, including people who arranged the table last year. */
  it('appends columns the stored layout has never seen', () => {
    const layout = reconcileLayout(known, { order: ['owner', 'name'] })

    expect(layout.order).toEqual(['owner', 'name', 'type', 'site'])
  })

  it('keeps the stored order of the columns it recognises', () => {
    expect(reconcileLayout(known, { order: ['site', 'owner', 'type', 'name'] }).order)
      .toEqual(['site', 'owner', 'type', 'name'])
  })
})

describe('toggleColumn', () => {
  it('hides and shows a column', () => {
    const hidden = toggleColumn(defaultLayout(known), 'type')
    expect(isColumnVisible(hidden, 'type')).toBe(false)
    expect(visibleColumns(hidden)).toEqual(['name', 'owner', 'site'])

    expect(isColumnVisible(toggleColumn(hidden, 'type'), 'type')).toBe(true)
  })

  /**
   * An empty table is not a view of anything, and the way back would be a menu with no table beside
   * it. The last visible column stays.
   */
  it('refuses to hide the last visible column', () => {
    let layout = defaultLayout(known)
    for (const id of ['type', 'owner', 'site'] as Id[]) layout = toggleColumn(layout, id)
    expect(visibleColumns(layout)).toEqual(['name'])

    expect(toggleColumn(layout, 'name')).toBe(layout)
  })
})

describe('moveColumn', () => {
  it('moves a column to where the target sits and shifts the rest along', () => {
    expect(moveColumn(defaultLayout(known), 'site', 'type').order)
      .toEqual(['name', 'site', 'type', 'owner'])
  })

  it('moves a column later as well as earlier', () => {
    expect(moveColumn(defaultLayout(known), 'name', 'owner').order)
      .toEqual(['type', 'owner', 'name', 'site'])
  })

  it('does nothing for a column dropped on itself or for an id it does not know', () => {
    const layout = defaultLayout(known)
    expect(moveColumn(layout, 'name', 'name')).toBe(layout)
    expect(moveColumn(layout, 'nope' as Id, 'name')).toBe(layout)
  })

  /** Order is independent of visibility, so a hidden column keeps its place for when it returns. */
  it('keeps a hidden column in the order it was moved to', () => {
    const layout = moveColumn(toggleColumn(defaultLayout(known), 'type'), 'type', 'name')

    expect(layout.order).toEqual(['type', 'name', 'owner', 'site'])
    expect(visibleColumns(layout)).toEqual(['name', 'owner', 'site'])
  })
})

describe('readLayout', () => {
  beforeEach(() => localStorage.clear())

  it('round-trips through storage', () => {
    const layout = moveColumn(toggleColumn(defaultLayout(known), 'site'), 'owner', 'name')
    writeLayout('assets:test', layout)

    expect(readLayout('assets:test', known)).toEqual(layout)
  })

  /** A corrupt preference must not take the page down with it. */
  it('falls back to the default when what is stored cannot be read', () => {
    localStorage.setItem('assets:test', '{ not json')

    expect(readLayout('assets:test', known)).toEqual(defaultLayout(known))
  })

  it('reconciles what it reads against the columns that exist now', () => {
    localStorage.setItem('assets:test', JSON.stringify({ order: ['site', 'ancient'], hidden: [] }))

    expect(readLayout('assets:test', known).order).toEqual(['site', 'name', 'type', 'owner'])
  })
})
