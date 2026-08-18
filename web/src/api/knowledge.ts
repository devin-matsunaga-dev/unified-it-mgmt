import { apiRequest } from './client'

/**
 * Where an article stands (WP-5.9). A status with an entry condition rather than a published flag beside a
 * state: `Published` is reachable only once the article has a summary and a body, because somebody who
 * finds an article stops looking.
 */
export type KbArticleStatus = 'Draft' | 'Published' | 'Archived'

/** One earlier version, with its prose — a list of dates nobody can open is a changelog, not a history. */
export type KbRevision = {
  version: number
  title: string
  summary: string
  body: string
  keywords: string | null
  authorId: string
  authorName: string
  /** When this version stopped being the current one. */
  createdAt: string
}

export type KbArticle = {
  id: string
  number: string
  title: string
  summary: string
  body: string
  keywords: string | null
  status: KbArticleStatus
  categoryId: string | null
  categoryName: string | null
  /** Set when the article began as the draft a closed problem prompted for. */
  problemId: string | null
  problemNumber: string | null
  version: number
  authorId: string
  authorName: string
  publishedById: string | null
  publishedByName: string | null
  /** When it last became visible. Read beside `status`, never instead of it. */
  publishedAt: string | null
  archivedAt: string | null
  createdAt: string
  updatedAt: string
  linkedTicketCount: number
  /**
   * Where this article can go from here, read off the record rather than duplicated in the browser —
   * WP-5.8's call, because a workflow copied into the SPA eventually withholds a button nobody knew to ask
   * for.
   */
  nextStatuses: KbArticleStatus[]
  /** Only on a single-article read, and only for an agent; the list leaves it undefined. */
  revisions?: KbRevision[] | null
}

export type KbArticlePage = { items: KbArticle[]; total: number; page: number; pageSize: number }

export type KbArticleFilter = {
  search?: string
  statuses?: KbArticleStatus[]
  categoryId?: string
  page?: number
  pageSize?: number
}

export type CreateKbArticleInput = {
  title: string
  summary: string
  body: string
  keywords?: string | null
  categoryId?: string | null
  problemId?: string | null
}

export type UpdateKbArticleInput = Omit<CreateKbArticleInput, 'problemId'>

/**
 * An article the knowledge base thinks is about what somebody is typing. `rank` is a `ts_rank` — carried
 * for ordering, never rendered as a percentage, because a rank is a number about one document against one
 * query and two of them are not comparable (WP-5.4).
 */
export type KbSuggestion = {
  id: string
  number: string
  title: string
  summary: string
  categoryName: string | null
  publishedAt: string | null
  rank: number
}

export type KbSuggestionInput = {
  subject?: string
  body?: string
  categoryId?: string | null
  limit?: number
}

/** An article attached to a ticket — usually the one it was resolved with. */
export type TicketKbArticle = {
  articleId: string
  number: string
  title: string
  summary: string
  status: KbArticleStatus
  linkedById: string
  linkedByName: string
  linkedAt: string
}

function articleQuery(filter: KbArticleFilter) {
  const query = new URLSearchParams()
  if (filter.search) query.set('search', filter.search)
  if (filter.statuses?.length) query.set('status', filter.statuses.join(','))
  if (filter.categoryId) query.set('categoryId', filter.categoryId)
  if (filter.page) query.set('page', String(filter.page))
  if (filter.pageSize) query.set('pageSize', String(filter.pageSize))
  return query.toString()
}

export const knowledgeApi = {
  list: (filter: KbArticleFilter = {}) =>
    apiRequest<KbArticlePage>(`/api/kb-articles?${articleQuery(filter)}`),
  get: (id: string) => apiRequest<KbArticle>(`/api/kb-articles/${id}`),
  create: (input: CreateKbArticleInput) =>
    apiRequest<KbArticle>('/api/kb-articles', { method: 'POST', body: JSON.stringify(input) }),
  update: (id: string, input: UpdateKbArticleInput) =>
    apiRequest<KbArticle>(`/api/kb-articles/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  transition: (id: string, targetStatus: KbArticleStatus) =>
    apiRequest<KbArticle>(`/api/kb-articles/${id}/transitions`, {
      method: 'POST',
      body: JSON.stringify({ targetStatus }),
    }),
  restore: (id: string, version: number) =>
    apiRequest<KbArticle>(`/api/kb-articles/${id}/revisions/${version}/restoration`, { method: 'POST' }),
  remove: (id: string) => apiRequest<void>(`/api/kb-articles/${id}`, { method: 'DELETE' }),

  suggest: (input: KbSuggestionInput) => {
    const query = new URLSearchParams()
    if (input.subject) query.set('subject', input.subject)
    if (input.body) query.set('body', input.body)
    if (input.categoryId) query.set('categoryId', input.categoryId)
    if (input.limit) query.set('limit', String(input.limit))
    return apiRequest<KbSuggestion[]>(`/api/kb-articles/suggestions?${query}`)
  },

  listForTicket: (ticketId: string) =>
    apiRequest<TicketKbArticle[]>(`/api/tickets/${ticketId}/kb-articles`),
  attachToTicket: (ticketId: string, articleId: string) =>
    apiRequest<TicketKbArticle>(`/api/tickets/${ticketId}/kb-articles`, {
      method: 'POST',
      body: JSON.stringify({ articleId }),
    }),
  detachFromTicket: (ticketId: string, articleId: string) =>
    apiRequest<void>(`/api/tickets/${ticketId}/kb-articles/${articleId}`, { method: 'DELETE' }),
}
