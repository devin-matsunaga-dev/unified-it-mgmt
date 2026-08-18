import type { DirectoryUser } from '../../api/directory'
import type { TicketFilter, TicketPriority, TicketQueue, TicketType } from '../../api/helpdesk'
import type { CategoryOption } from './categoryFields'
import { displayStatus, ticketPriorities, ticketStatuses } from './ticketUi'

/**
 * What each control needs to draw itself. Passed in rather than reached for, so this file stays a
 * description of the filter bar and does no data fetching of its own.
 */
export type TicketFilterContext = {
  filter: TicketFilter
  patchFilter: (patch: Partial<TicketFilter>) => void
  queues: TicketQueue[]
  categoryOptions: CategoryOption[]
  /** Who a ticket can be assigned to, already narrowed to the people who take work. */
  assignees: DirectoryUser[]
}

export type TicketFilterId = 'kind' | 'statuses' | 'priorities' | 'queueId' | 'categoryId' | 'assignee'

/** The dropdown value meaning "nobody has it", as distinct from "anybody may". */
export const unassignedValue = 'unassigned'

export type TicketFilterDefinition = {
  id: TicketFilterId
  label: string
  render: (context: TicketFilterContext) => React.ReactNode
}

const kinds: { label: string; value: TicketType }[] = [
  { label: 'Incidents', value: 'Incident' },
  { label: 'Service requests', value: 'ServiceRequest' },
]

/**
 * The filter bar, defined once so it can be chosen from.
 *
 * Search is deliberately absent: it is the primary way into this list rather than a narrowing of it,
 * and a list whose search box somebody has hidden is one they cannot get back to without finding a
 * menu.
 */
export const ticketFilterDefinitions: readonly TicketFilterDefinition[] = [
  {
    id: 'kind',
    label: 'Incident / service request',
    /**
     * A select rather than a segmented control. Three side-by-side buttons were the widest thing in
     * the bar and pushed the column chooser onto a second row; this reads the same and costs the
     * width of one control, like every other filter beside it.
     */
    render: ({ filter, patchFilter }) => <select aria-label="Filter by kind" className="input w-auto min-w-40"
      value={filter.type ?? ''}
      onChange={(event) => patchFilter({ type: (event.target.value || undefined) as TicketType | undefined })}>
      <option value="">All kinds</option>
      {kinds.map(({ label, value }) => <option key={value} value={value}>{label}</option>)}
    </select>,
  },
  {
    id: 'statuses',
    label: 'Status',
    render: ({ filter, patchFilter }) => <select aria-label="Filter by status" className="input w-auto min-w-36"
      value={filter.statuses?.[0] ?? ''}
      onChange={(event) => patchFilter({ statuses: event.target.value ? [event.target.value] : undefined })}>
      <option value="">All statuses</option>
      {ticketStatuses.map((status) => <option key={status} value={status}>{displayStatus(status)}</option>)}
    </select>,
  },
  {
    id: 'priorities',
    label: 'Priority',
    render: ({ filter, patchFilter }) => <select aria-label="Filter by priority" className="input w-auto min-w-36"
      value={filter.priorities?.[0] ?? ''}
      onChange={(event) => patchFilter({ priorities: event.target.value ? [event.target.value as TicketPriority] : undefined })}>
      <option value="">All priorities</option>
      {ticketPriorities.map((priority) => <option key={priority}>{priority}</option>)}
    </select>,
  },
  {
    id: 'queueId',
    label: 'Queue',
    render: ({ filter, patchFilter, queues }) => <select aria-label="Filter by queue" className="input w-auto min-w-36"
      value={filter.queueId ?? ''}
      onChange={(event) => patchFilter({ queueId: event.target.value || undefined })}>
      <option value="">All queues</option>
      {queues.map((queue) => <option key={queue.id} value={queue.id}>{queue.name}</option>)}
    </select>,
  },
  {
    id: 'categoryId',
    label: 'Category',
    render: ({ filter, patchFilter, categoryOptions }) => <select aria-label="Filter by category" className="input w-auto min-w-36"
      value={filter.categoryId ?? ''}
      onChange={(event) => patchFilter({ categoryId: event.target.value || undefined })}>
      <option value="">All categories</option>
      {categoryOptions.map((option) => <option key={option.id} value={option.id}>{' '.repeat(option.depth * 2)}{option.name}</option>)}
    </select>,
  },
  {
    id: 'assignee',
    label: 'Assigned to',
    /**
     * One control for both halves of the same question. `unassigned` and `assignedTechnicianId` are
     * already mutually exclusive in `normalizeFilter` — unassigned wins — and a checkbox beside a
     * name picker left that exclusivity implicit and easy to contradict.
     *
     * The value is the username, not the user id: a ticket records the identity the helpdesk was
     * given, which is the username, and the people page filters by it the same way.
     */
    render: ({ filter, patchFilter, assignees }) => <select aria-label="Filter by assignee"
      className="input w-auto min-w-40"
      value={filter.unassigned ? unassignedValue : filter.assignedTechnicianId ?? ''}
      onChange={(event) => {
        const chosen = event.target.value
        patchFilter({
          unassigned: chosen === unassignedValue ? true : undefined,
          assignedTechnicianId: chosen === '' || chosen === unassignedValue ? undefined : chosen,
        })
      }}>
      <option value="">Anyone</option>
      <option value={unassignedValue}>Unassigned</option>
      {assignees.map((user) => <option key={user.id} value={user.username}>{user.displayName}</option>)}
    </select>,
  },
]

export const ticketFilterIds = ticketFilterDefinitions.map((definition) => definition.id)

export function ticketFilterDefinition(id: TicketFilterId): TicketFilterDefinition {
  const found = ticketFilterDefinitions.find((definition) => definition.id === id)
  if (!found) throw new Error(`Unknown ticket filter '${id}'.`)
  return found
}

/**
 * The part of a filter a hidden control owns, cleared when it is hidden.
 *
 * A filter still narrowing the list from behind a control nobody can see is the worst outcome here:
 * the list would show a subset with no visible reason, and the way to widen it would be to find the
 * chooser and turn the control back on.
 */
export function clearTicketFilter(id: TicketFilterId, filter: TicketFilter): TicketFilter {
  switch (id) {
    case 'kind': return { ...filter, type: undefined }
    case 'statuses': return { ...filter, statuses: undefined }
    case 'priorities': return { ...filter, priorities: undefined }
    case 'queueId': return { ...filter, queueId: undefined }
    case 'categoryId': return { ...filter, categoryId: undefined }
    // Both halves go: the control owned the whole question of who has the ticket.
    case 'assignee': return { ...filter, unassigned: undefined, assignedTechnicianId: undefined }
  }
}
