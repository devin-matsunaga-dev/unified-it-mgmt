import { apiRequest } from './client'

/**
 * Where a change stands. Approved, Rejected and Cancelled are terminal — approving has already put a
 * maintenance window into monitoring, and unpicking that is a decision of its own rather than an arrow.
 */
export type ChangeStatus = 'Draft' | 'Submitted' | 'Approved' | 'Rejected' | 'Cancelled'

/** One configuration item a change covers. `name` is null only for a CI that has been deleted. */
export type ChangeCi = {
  ciId: string
  name: string | null
  type: string | null
  assetTag: string | null
  lifecycleState: string | null
  /** True when the dependency walk added it at approval rather than the requester naming it. */
  isDependent: boolean
}

export type Change = {
  id: string
  number: string
  title: string
  description: string
  status: ChangeStatus
  plannedStartAt: string
  plannedEndAt: string
  includeDependents: boolean
  requestedById: string
  requestedByName: string
  requestedAt: string
  decidedById: string | null
  decidedByName: string | null
  decidedAt: string | null
  decisionNote: string | null
  updatedAt: string
  ciCount: number
  dependentCount: number
  /** What the server will accept next. The board reads this rather than holding its own workflow. */
  nextStatuses: ChangeStatus[]
  /** Only on a single-change read; the list leaves it null. */
  cis?: ChangeCi[] | null
}

export type ChangePage = { items: Change[]; total: number; page: number; pageSize: number }

export type ChangeFilter = {
  search?: string
  statuses?: ChangeStatus[]
  ciId?: string
  /** Both bounds are ISO instants. A change is in range when its window overlaps it at all. */
  from?: string
  to?: string
  page?: number
  pageSize?: number
}

export type ChangeInput = {
  title: string
  description: string
  plannedStartAt: string
  plannedEndAt: string
  ciIds: string[]
  includeDependents: boolean
}

function query(filter: ChangeFilter) {
  const search = new URLSearchParams()
  if (filter.search) search.set('search', filter.search)
  if (filter.statuses?.length) search.set('status', filter.statuses.join(','))
  if (filter.ciId) search.set('ciId', filter.ciId)
  if (filter.from) search.set('from', filter.from)
  if (filter.to) search.set('to', filter.to)
  if (filter.page) search.set('page', String(filter.page))
  if (filter.pageSize) search.set('pageSize', String(filter.pageSize))
  const rendered = search.toString()
  return rendered ? `?${rendered}` : ''
}

export const changesApi = {
  list: (filter: ChangeFilter = {}) => apiRequest<ChangePage>(`/api/changes${query(filter)}`),

  get: (id: string) => apiRequest<Change>(`/api/changes/${id}`),

  create: (input: ChangeInput) =>
    apiRequest<Change>('/api/changes', { method: 'POST', body: JSON.stringify(input) }),

  update: (id: string, input: ChangeInput) =>
    apiRequest<Change>(`/api/changes/${id}`, { method: 'PUT', body: JSON.stringify(input) }),

  transition: (id: string, targetStatus: ChangeStatus, note?: string) =>
    apiRequest<Change>(`/api/changes/${id}/transitions`, {
      method: 'POST',
      body: JSON.stringify({ targetStatus, note: note || null }),
    }),
}
