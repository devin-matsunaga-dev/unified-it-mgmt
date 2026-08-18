import type { TicketFilter } from '../../api/helpdesk'

/**
 * The views the app ships with.
 *
 * Defined here rather than seeded into the database, which is where "Unassigned high priority" used
 * to live. A seeded view belongs to whoever seeded it and is shared, so the delete button — which
 * only appears on your own views — never showed for anybody else, and the one view nobody could
 * remove was one of only two on offer.
 *
 * As definitions they are hidden and restored per browser instead, like the column and filter menus
 * beside them, and no database row is involved either way.
 */
export type BuiltInView = {
  id: string
  label: string
  /**
   * The filter it means, given who is signed in. A function rather than a value because one of them
   * is about the reader — and an empty result means "everything", which is what All tickets means.
   */
  filter: (username: string) => TicketFilter
  /** Why it is worth having, shown in the menu that restores it. */
  description: string
  /** True when the view is meaningless without a signed-in username, so it is not offered without one. */
  needsUser?: boolean
}

export const builtInViews: readonly BuiltInView[] = [
  {
    id: 'all',
    label: 'All tickets',
    filter: () => ({}),
    description: 'Everything, unfiltered.',
  },
  {
    id: 'mine',
    label: 'My tickets',
    // Matched on the username, not the OIDC subject: a ticket records the identity the helpdesk was
    // given, and Keycloak's subject id matches nothing it stored.
    filter: (username) => ({ assignedTechnicianId: username }),
    description: 'Assigned to you.',
    needsUser: true,
  },
  {
    id: 'unassigned-high',
    label: 'Unassigned high priority',
    filter: () => ({ priorities: ['Critical', 'High'], unassigned: true }),
    description: 'Urgent work nobody has picked up.',
  },
  {
    // The intake bucket: what has arrived and not yet been looked at.
    id: 'needs-triage',
    label: 'Needs triage',
    filter: () => ({ statuses: ['New', 'Triage'] }),
    description: 'Raised but not yet worked.',
  },
  {
    // Pending is the status the SLA clock pauses on (WP-1.5), so this is the pile that stops moving
    // without anybody noticing.
    id: 'awaiting-customer',
    label: 'Awaiting customer',
    filter: () => ({ statuses: ['Pending'] }),
    description: 'Waiting on a reply, and off the SLA clock.',
  },
]

export const builtInViewIds = builtInViews.map((view) => view.id)

/** The views worth offering to this reader: everything, minus those that need a name we do not have. */
export function offerableViews(username: string): BuiltInView[] {
  return builtInViews.filter((view) => !view.needsUser || username !== '')
}

export function builtInView(id: string): BuiltInView | undefined {
  return builtInViews.find((view) => view.id === id)
}

export const builtInViewLayoutKey = 'tickets:views'
