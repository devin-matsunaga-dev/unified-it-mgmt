import { describe, expect, it } from 'vitest'
import { defaultView, topologyViews, viewById } from './views'

describe('topologyViews', () => {
  it('offers the five progressive views in order', () => {
    expect(topologyViews.map((view) => view.label))
      .toEqual(['Overview', 'Network', 'Infrastructure', 'Applications', 'Everything'])
  })

  /** §1: Overview is the preferred default and must not open onto a wall of laptops. */
  it('opens on Overview, which excludes endpoint hardware', () => {
    expect(defaultView.id).toBe('overview')
    expect(defaultView.types).not.toContain('Hardware')
  })

  /** Only Everything asks for no filter; every other view is a cut made on the server. */
  it('filters on the server for every view but Everything', () => {
    for (const view of topologyViews) {
      if (view.id === 'everything') expect(view.types).toBeNull()
      else expect(view.types?.length).toBeGreaterThan(0)
    }
  })

  /**
   * An infrastructure map that cannot show which switch a hypervisor hangs off, or an application
   * map that cannot show where the application runs, answers half the question it was opened for.
   */
  it('keeps the dependencies each view needs to be readable', () => {
    expect(viewById('infrastructure').types).toContain('NetworkDevice')
    expect(viewById('applications').types).toEqual(expect.arrayContaining(['Server', 'Virtual']))
  })

  it('falls back to the default for an id nobody offers', () => {
    expect(viewById('nonsense')).toBe(defaultView)
  })

  it('gives every view a description, because the buttons alone do not say what is cut', () => {
    for (const view of topologyViews) expect(view.description.length).toBeGreaterThan(10)
  })
})
