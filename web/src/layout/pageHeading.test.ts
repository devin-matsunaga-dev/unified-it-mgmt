import { describe, expect, it } from 'vitest'
import { headingForPath } from './pageHeading'

describe('headingForPath', () => {
  it('gives a list route its title and subtitle', () => {
    expect(headingForPath('/assets')).toEqual({
      title: 'Assets',
      subtitle: 'The configuration items every ticket, alert, and device links back to.',
    })
  })

  it('gives the index route the overview heading', () => {
    expect(headingForPath('/').title).toBe('Overview')
  })

  /** A parent's heading must not leak onto its children — the bug the screens showed was exactly this. */
  it('does not let a parent route answer for a child', () => {
    expect(headingForPath('/assets/drift').title).toBe('Drift')
    expect(headingForPath('/software/licenses').title).toBe('Licences')
    expect(headingForPath('/admin/settings/sla').title).toBe('Service levels')
  })

  /** `/assets/import` and `/assets/:id` both match; the literal one is listed first and must win. */
  it('prefers a literal route over the parameterised one it would fall into', () => {
    expect(headingForPath('/assets/import').title).toBe('Import configuration items')
    expect(headingForPath('/assets/2f6f9c1e-0000-4000-8000-000000000000').title).toBe('Configuration item')
    expect(headingForPath('/contracts/vendors').title).toBe('Vendors')
  })

  it('leaves a record route without a subtitle, because the page supplies one', () => {
    expect(headingForPath('/tickets/1')).toEqual({ title: 'Ticket', subtitle: undefined })
  })

  it('falls back to the not-found heading for a path no route claims', () => {
    expect(headingForPath('/nothing/here')).toEqual({ title: 'Page not found' })
  })
})
