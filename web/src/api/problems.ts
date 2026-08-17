import { apiRequest } from './client'
import type { TicketPriority } from './helpdesk'

/**
 * Where a problem stands. A status rather than a status plus a known-error flag: the two together
 * describe eight states of which most are nonsense, and `KnownError` is a state with an entry condition
 * — a root cause and a workaround — which is what makes the known-error list worth searching.
 */
export type ProblemStatus = 'Investigating' | 'KnownError' | 'Resolved' | 'Closed'

/** What a run of incidents was counted against. */
export type ProblemSubjectScope = 'Ci' | 'Category'

export type ProblemSuggestionStatus = 'Open' | 'Accepted' | 'Dismissed'

/** The CI or category a problem is about. `name` is null when the CI has since been deleted. */
export type ProblemSubject = {
  scope: ProblemSubjectScope
  id: string
  name: string | null
  type: string | null
}

export type ProblemIncident = {
  ticketId: string
  number: string
  title: string
  status: string
  priority: TicketPriority
  createdAt: string
  linkedById: string
  linkedByName: string
  linkedAt: string
}

export type Problem = {
  id: string
  number: string
  title: string
  description: string
  status: ProblemStatus
  priority: TicketPriority
  isKnownError: boolean
  subject: ProblemSubject | null
  rootCause: string | null
  workaround: string | null
  resolution: string | null
  assignedTechnicianId: string | null
  openedById: string
  openedByName: string
  incidentCount: number
  createdAt: string
  updatedAt: string
  knownErrorAt: string | null
  resolvedAt: string | null
  closedAt: string | null
  /** Only on a single-problem read; the list leaves it undefined. */
  incidents?: ProblemIncident[] | null
}

export type ProblemPage = { items: Problem[]; total: number; page: number; pageSize: number }

export type ProblemFilter = {
  search?: string
  statuses?: ProblemStatus[]
  knownErrorsOnly?: boolean
  ciId?: string
  categoryId?: string
  page?: number
  pageSize?: number
}

export type CreateProblemInput = {
  title: string
  description: string
  priority: TicketPriority
  ciId?: string | null
  categoryId?: string | null
  rootCause?: string | null
  workaround?: string | null
  assignedTechnicianId?: string | null
  incidentIds?: string[]
}

export type UpdateProblemInput = Omit<CreateProblemInput, 'incidentIds'>

/** One symptom people actually reported, and how many of them said it. */
export type KnowledgeDraftSymptom = { text: string; incidentCount: number }

/**
 * The article somebody would write about a closed problem, pre-filled from what they just typed. WP-5.9
 * owns the knowledge base; nothing here stores one.
 */
export type KnowledgeDraft = {
  problemId: string
  problemNumber: string
  title: string
  subjectName: string | null
  symptoms: KnowledgeDraftSymptom[]
  rootCause: string | null
  workaround: string | null
  resolution: string | null
  incidentNumbers: string[]
}

/** `knowledgeDraft` is present only when the transition was a close. */
export type ProblemTransitionResult = { problem: Problem; knowledgeDraft: KnowledgeDraft | null }

export type ProblemSuggestion = {
  id: string
  scope: ProblemSubjectScope
  subject: ProblemSubject
  incidentCount: number
  windowStart: string
  windowEnd: string
  status: ProblemSuggestionStatus
  detectedAt: string
  createdProblemId: string | null
  createdProblemNumber: string | null
  resolvedById: string | null
  resolvedByName: string | null
  resolvedAt: string | null
  dismissReason: string | null
}

/** What one pass of the detector did. `skipped` is keyed by the reason it stayed quiet. */
export type ProblemDetectionRun = {
  windowStart: string
  windowEnd: string
  minimumIncidents: number
  examined: number
  suggested: number
  skipped: Record<string, number>
  suggestions: ProblemSuggestion[]
}

function problemQuery(filter: ProblemFilter) {
  const query = new URLSearchParams()
  if (filter.search) query.set('search', filter.search)
  if (filter.statuses?.length) query.set('status', filter.statuses.join(','))
  if (filter.knownErrorsOnly) query.set('knownErrorsOnly', 'true')
  if (filter.ciId) query.set('ciId', filter.ciId)
  if (filter.categoryId) query.set('categoryId', filter.categoryId)
  if (filter.page) query.set('page', String(filter.page))
  if (filter.pageSize) query.set('pageSize', String(filter.pageSize))
  return query.toString()
}

export const problemsApi = {
  list: (filter: ProblemFilter = {}) => apiRequest<ProblemPage>(`/api/problems?${problemQuery(filter)}`),
  get: (id: string) => apiRequest<Problem>(`/api/problems/${id}`),
  create: (input: CreateProblemInput) =>
    apiRequest<Problem>('/api/problems', { method: 'POST', body: JSON.stringify(input) }),
  update: (id: string, input: UpdateProblemInput) =>
    apiRequest<Problem>(`/api/problems/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  transition: (id: string, targetStatus: ProblemStatus, resolution?: string | null) =>
    apiRequest<ProblemTransitionResult>(`/api/problems/${id}/transitions`, {
      method: 'POST',
      body: JSON.stringify({ targetStatus, resolution: resolution ?? null }),
    }),
  getKnowledgeDraft: (id: string) => apiRequest<KnowledgeDraft>(`/api/problems/${id}/knowledge-draft`),
  linkIncident: (id: string, ticketId: string) =>
    apiRequest<Problem>(`/api/problems/${id}/incidents`, { method: 'POST', body: JSON.stringify({ ticketId }) }),
  unlinkIncident: (id: string, ticketId: string) =>
    apiRequest<void>(`/api/problems/${id}/incidents/${ticketId}`, { method: 'DELETE' }),
  /** The other half of the link, read from the incident. */
  listForTicket: (ticketId: string) => apiRequest<Problem[]>(`/api/tickets/${ticketId}/problems`),

  listSuggestions: (status?: ProblemSuggestionStatus) =>
    apiRequest<ProblemSuggestion[]>(`/api/problem-suggestions${status ? `?status=${status}` : ''}`),
  detect: () => apiRequest<ProblemDetectionRun>('/api/problem-suggestions/detect', { method: 'POST' }),
  acceptSuggestion: (id: string, input: { title?: string; priority?: TicketPriority } = {}) =>
    apiRequest<ProblemSuggestion>(`/api/problem-suggestions/${id}/acceptance`, {
      method: 'POST',
      body: JSON.stringify(input),
    }),
  dismissSuggestion: (id: string, reason?: string) =>
    apiRequest<ProblemSuggestion>(`/api/problem-suggestions/${id}/dismissal`, {
      method: 'POST',
      body: JSON.stringify({ reason: reason ?? null }),
    }),
}
