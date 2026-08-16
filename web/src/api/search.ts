import { apiRequest } from './client'

/**
 * The kinds global search reaches (WP-5.4). WP-5.9's knowledge base adds a sixth on both sides at once —
 * there is deliberately no member for it while nothing implements it, because a group that always reports
 * "nothing found" answers a real question with a confident no.
 */
export type SearchResultType = 'Ticket' | 'Ci' | 'Device' | 'Alert' | 'User'

/**
 * Why a group is empty, which is three different statements and never one.
 *
 * - `Searched` — it ran; a zero is a fact about the estate.
 * - `NotRequested` — the type filter excluded it, so it was never queried.
 * - `NotPermitted` — this signed-in user may not read that kind of record at all.
 */
export type SearchSourceStatus = 'Searched' | 'NotRequested' | 'NotPermitted'

export type SearchHit = {
  type: SearchResultType
  id: string
  title: string
  /** The identifier a person quotes: a ticket number, an asset tag, a username. */
  reference: string | null
  /** One line of context — who raised it, where it lives, what it is attached to. */
  subtitle: string | null
  /** The one status word this kind is triaged by, as a raw token this app labels and colours. */
  badge: string | null
}

export type SearchGroup = {
  type: SearchResultType
  status: SearchSourceStatus
  returned: number
  /** Everything this kind matched, cap or no cap — so a list of five can say ninety. */
  total: number
  truncated: boolean
  hits: SearchHit[]
}

export type SearchSummary = { returnedCount: number; totalCount: number; truncated: boolean }

export type SearchResults = {
  term: string
  /** The per-kind cap the server applied, which it may have clamped. */
  limit: number
  types: SearchResultType[]
  summary: SearchSummary
  groups: SearchGroup[]
}

export const searchApi = {
  search: (term: string, limit?: number) => {
    const query = new URLSearchParams({ q: term })
    if (limit !== undefined) query.set('limit', String(limit))
    return apiRequest<SearchResults>(`/api/search?${query}`)
  },
}
