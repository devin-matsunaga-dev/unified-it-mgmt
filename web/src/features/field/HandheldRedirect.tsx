import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { useIsHandheld } from './handheld'

/**
 * Desktop routes that have a field equivalent worth preserving. Anything not listed falls back to the
 * scan screen: landing a technician somewhere they can start work beats landing them on a page the
 * field surface has no version of.
 */
const equivalents: [RegExp, (match: RegExpMatchArray) => string][] = [
  [/^\/assets\/([^/]+)\/?$/, (match) => `/field/ci/${match[1]}`],
  [/^\/scan\/?$/, () => '/field/scan'],
  [/^\/audits\/([^/]+)\/?$/, (match) => `/field/audits/${match[1]}`],
  [/^\/audits\/?$/, () => '/field/audits'],
  [/^\/tickets\/([^/]+)\/?$/, (match) => `/field/tickets/${match[1]}`],
  [/^\/tickets\/?$/, () => '/field/tickets'],
]

export function fieldPathFor(pathname: string): string {
  for (const [pattern, build] of equivalents) {
    const match = pathname.match(pattern)
    if (match) return build(match)
  }
  return '/field/scan'
}

/**
 * Holds a handset inside the field surface. The agent shell is built to a 1280px floor (DESIGN.md
 * §10) and using it on a phone is not merely awkward — a mis-tap on a dense table is a write to the
 * CMDB. So this is a wall, not a suggestion: there is deliberately no "open the full page" escape,
 * because an escape is the thing a technician finds by accident at the worst moment.
 *
 * The consequence is stated plainly in DESIGN.md §9 and worth repeating: anything a technician needs
 * in the field has to exist on the field surface, because they cannot route around a gap.
 */
export function HandheldRedirect({ children }: { children: ReactNode }) {
  const { pathname } = useLocation()
  const isHandheld = useIsHandheld()
  if (isHandheld) return <Navigate to={fieldPathFor(pathname)} replace />
  return children
}
