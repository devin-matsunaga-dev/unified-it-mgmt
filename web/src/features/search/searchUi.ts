import { AlertTriangle, BookOpen, Boxes, Contact, Headphones, MonitorCog } from 'lucide-react'
import type { SearchGroup, SearchHit, SearchResultType, SearchResults } from '../../api/search'

/**
 * Everything about the results list that is a decision rather than a rendering (WP-5.4). Kept out of the
 * component so the keyboard's idea of "the next result" and the screen's idea of it come from one function
 * — the two disagreeing is how an arrow key opens the wrong record.
 */

const groupLabels: Record<SearchResultType, string> = {
  Ticket: 'Tickets',
  Ci: 'Assets',
  Device: 'Devices',
  Alert: 'Alerts',
  User: 'People',
  KbArticle: 'Knowledge',
}

export const groupIcons: Record<SearchResultType, typeof Boxes> = {
  Ticket: Headphones,
  Ci: Boxes,
  Device: MonitorCog,
  Alert: AlertTriangle,
  User: Contact,
  KbArticle: BookOpen,
}

export function searchGroupLabel(type: SearchResultType) {
  return groupLabels[type] ?? type
}

/**
 * Where a result opens. Routes are the SPA's own business, so the server sends a kind and an id and this is
 * the one place that turns them into a path — the same split WP-5.3 made when the timeline sent a ticket id
 * rather than a link.
 *
 * These are the agent routes, which is correct because the bar lives in the agent shell: a user with only
 * the EndUser role never reaches `AppShell` at all (`HomeRoute` sends them to the portal).
 *
 * It takes only the kind and the id — everything it has ever needed — so that WP-5.5's widgets can link at
 * a record through this same function rather than keeping a second copy of the map. A widget and the search
 * box disagreeing about where an alert opens is exactly what that copy would eventually cause.
 */
export function searchResultHref(hit: Pick<SearchHit, 'type' | 'id'>) {
  switch (hit.type) {
    case 'Ticket':
      return `/tickets/${hit.id}`
    case 'Ci':
      return `/assets/${hit.id}`
    case 'Device':
      return `/monitoring/devices/${hit.id}`
    // The board's existing deep link, the same one WP-3.10's notifications and WP-5.3's timeline arrive at,
    // so an alert found here opens the same drawer as an alert found anywhere else.
    case 'Alert':
      return `/monitoring/alerts?alertId=${hit.id}`
    case 'User':
      return `/people/${hit.id}`
    // The agent route, like every other one here: this bar lives in the agent shell, and the portal has its
    // own reader at /portal/kb/:id.
    case 'KbArticle':
      return `/knowledge/${hit.id}`
    default:
      return '/'
  }
}

/**
 * The groups worth drawing, in server order.
 *
 * A group the caller may not read is dropped rather than rendered empty: telling somebody "Assets — nothing
 * found" when the truth is that they may not search assets is a claim about the estate rather than about
 * their account. `NotRequested` cannot occur while this bar sends no type filter, and is dropped for the
 * same reason.
 */
export function visibleGroups(results: SearchResults): SearchGroup[] {
  return results.groups.filter((group) => group.status === 'Searched' && group.hits.length > 0)
}

/**
 * Every hit in the order it is drawn — which is what the arrow keys move through. One flat list across the
 * group headings, because a reader pressing Down at the bottom of Tickets expects the first asset and not a
 * dead end.
 */
export function flattenHits(results: SearchResults): SearchHit[] {
  return visibleGroups(results).flatMap((group) => group.hits)
}

/** A stable per-hit key and DOM id. Two kinds can share an id only if a Guid repeats across modules. */
export function hitKey(hit: SearchHit) {
  return `${hit.type}-${hit.id}`
}

/**
 * Moves the highlight, wrapping at both ends.
 *
 * Wrapping rather than stopping: the list is short and the alternative is a key that silently does nothing,
 * which reads as a broken control. `-1` means nothing is highlighted yet, so the first Down lands on the
 * first result and the first Up on the last.
 */
export function moveHighlight(current: number, delta: number, count: number) {
  if (count === 0) return -1
  if (current < 0) return delta > 0 ? 0 : count - 1
  return (current + delta + count) % count
}

/**
 * What the footer says about how much was left out. Null when nothing was — an empty sentence is better
 * than "showing 3 of 3", which invites the reader to wonder what they are missing.
 */
export function describeTruncation(results: SearchResults) {
  if (!results.summary.truncated) return null
  const kinds = visibleGroups(results)
    .filter((group) => group.truncated)
    .map((group) => `${group.total} ${searchGroupLabel(group.type).toLowerCase()}`)
  return kinds.length === 0
    ? `${results.summary.totalCount} matches in all`
    : `${kinds.join(', ')} in all — keep typing to narrow it`
}

/**
 * The label under an empty result set. Two sentences and not one: nothing matched is a fact about the
 * estate, while a term this short was never sent anywhere.
 */
export const minimumTermLength = 2
