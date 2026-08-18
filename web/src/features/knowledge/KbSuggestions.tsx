import { useQuery } from '@tanstack/react-query'
import { BookOpen, ExternalLink } from 'lucide-react'
import { useEffect, useState } from 'react'
import { knowledgeApi, type KbSuggestion } from '../../api/knowledge'

/**
 * How long the typing has to stop before the knowledge base is asked. Long enough that a sentence is one
 * request rather than forty, short enough that the panel appears while somebody is still looking at the
 * form they typed it into.
 */
export const suggestionDebounceMs = 400

/** Below this there is nothing worth matching on, and asking would return the whole knowledge base's noise. */
const minimumSubjectLength = 6

/**
 * Articles that look like what somebody is typing, asked for after they stop.
 *
 * A hook rather than a component so the two callers can draw it differently: an agent gets a quiet list
 * beside the form, and a requester gets the deflection prompt, which is the same data asking a different
 * question.
 */
export function useKbSuggestions({ subject, body, categoryId, enabled = true, limit = 5 }: {
  subject: string
  body?: string
  categoryId?: string | null
  enabled?: boolean
  limit?: number
}) {
  const [debounced, setDebounced] = useState({ subject, body })

  useEffect(() => {
    const timer = setTimeout(() => setDebounced({ subject, body }), suggestionDebounceMs)
    return () => clearTimeout(timer)
  }, [subject, body])

  const ask = enabled && debounced.subject.trim().length >= minimumSubjectLength

  return useQuery({
    queryKey: ['kb-suggestions', debounced.subject, debounced.body ?? '', categoryId ?? '', limit],
    queryFn: () => knowledgeApi.suggest({
      subject: debounced.subject,
      body: debounced.body,
      categoryId,
      limit,
    }),
    enabled: ask,
    // A suggestion panel must never be the thing that fails a form. It is an aside; if the read breaks, the
    // panel stays quiet and the ticket is still submitted.
    retry: false,
    meta: { suppressErrorToast: true },
  })
}

/**
 * The agent's while-typing panel. Rendered only when there is something to show — an empty "no suggestions"
 * box beside a half-typed ticket is noise, and unlike a board there is no question here it answers.
 */
export function KbSuggestionList({ suggestions, onOpen }: {
  suggestions: KbSuggestion[]
  onOpen?: (suggestion: KbSuggestion) => void
}) {
  if (suggestions.length === 0) return null

  return <section className="rounded-lg border border-blue-200 bg-blue-50/60 p-4 dark:border-blue-900 dark:bg-blue-950/30">
    <h3 className="flex items-center gap-2 text-[13px] font-medium text-blue-800 dark:text-blue-300">
      <BookOpen size={16} />Knowledge that may already answer this
    </h3>
    <ul className="mt-2 space-y-2">
      {suggestions.map((suggestion) => <li key={suggestion.id}>
        <button
          type="button"
          className="w-full rounded-md px-2 py-1.5 text-left hover:bg-white/70 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:hover:bg-slate-900/50"
          onClick={() => onOpen?.(suggestion)}>
          <span className="flex items-baseline gap-2">
            <span className="min-w-0 flex-1 truncate text-sm font-medium text-slate-900 dark:text-slate-100">{suggestion.title}</span>
            <span className="shrink-0 font-mono text-[11px] text-slate-500">{suggestion.number}</span>
            {onOpen && <ExternalLink size={13} className="shrink-0 text-slate-400" />}
          </span>
          <span className="mt-0.5 block text-xs text-slate-600 dark:text-slate-300">{suggestion.summary}</span>
        </button>
      </li>)}
    </ul>
  </section>
}
