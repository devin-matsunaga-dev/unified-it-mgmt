import type { TicketFilter } from '../../api/helpdesk'

export const emptyFilter: TicketFilter = {}

/** Drops empty members so two filters that mean the same thing compare equal and serialise identically. */
export function normalizeFilter(filter: TicketFilter): TicketFilter {
  const search = filter.search?.trim()
  const normalized: TicketFilter = {}
  if (search) normalized.search = search
  if (filter.statuses?.length) normalized.statuses = [...filter.statuses].sort()
  if (filter.priorities?.length) normalized.priorities = [...filter.priorities].sort()
  if (filter.type) normalized.type = filter.type
  if (filter.queueId) normalized.queueId = filter.queueId
  if (filter.categoryId) normalized.categoryId = filter.categoryId
  if (filter.unassigned) normalized.unassigned = true
  else if (filter.assignedTechnicianId) normalized.assignedTechnicianId = filter.assignedTechnicianId
  if (filter.ciId) normalized.ciId = filter.ciId
  if (filter.requesterId) normalized.requesterId = filter.requesterId
  return normalized
}

export function filtersEqual(left: TicketFilter, right: TicketFilter): boolean {
  return JSON.stringify(normalizeFilter(left)) === JSON.stringify(normalizeFilter(right))
}

export function isFilterActive(filter: TicketFilter): boolean {
  return Object.keys(normalizeFilter(filter)).length > 0
}

/** Builds the `/api/tickets` query string; the API names its parameters q/status/priority/assignee. */
export function filterToQuery(filter: TicketFilter, page = 1, pageSize = 200): string {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  const normalized = normalizeFilter(filter)
  if (normalized.search) params.set('q', normalized.search)
  for (const status of normalized.statuses ?? []) params.append('status', status)
  for (const priority of normalized.priorities ?? []) params.append('priority', priority)
  if (normalized.type) params.set('type', normalized.type)
  if (normalized.queueId) params.set('queueId', normalized.queueId)
  if (normalized.categoryId) params.set('categoryId', normalized.categoryId)
  if (normalized.unassigned) params.set('unassigned', 'true')
  else if (normalized.assignedTechnicianId) params.set('assignee', normalized.assignedTechnicianId)
  if (normalized.ciId) params.set('ciId', normalized.ciId)
  if (normalized.requesterId) params.set('requester', normalized.requesterId)
  return params.toString()
}
