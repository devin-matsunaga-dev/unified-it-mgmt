import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BookOpen, Paperclip, Plus, X } from 'lucide-react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { knowledgeApi } from '../../api/knowledge'
import { Button } from '../../components/ui/Button'
import { formatLocal } from '../tickets/ticketUi'
import { KbStatusPill } from './knowledgeUi'
import { useKbSuggestions } from './KbSuggestions'

/**
 * Knowledge on a ticket: what is already attached, and what the knowledge base thinks would answer it.
 *
 * The two are one card rather than two, because attaching an article is nearly always the second half of
 * reading a suggestion — a technician who has just found the answer is the person best placed to say that
 * it was the answer, and asking them to go and look for it again on another screen is how that never
 * happens.
 */
export function TicketKnowledgeCard({ ticketId, subject, body, categoryId }: {
  ticketId: string
  subject: string
  body: string
  categoryId?: string | null
}) {
  const queryClient = useQueryClient()

  const attached = useQuery({
    queryKey: ['tickets', ticketId, 'kb-articles'],
    queryFn: () => knowledgeApi.listForTicket(ticketId),
    enabled: Boolean(ticketId),
  })

  // The ticket's own words, not something being typed — so this asks once and the debounce never fires
  // twice. Same read as the create form, which is what keeps the two lists consistent.
  const suggestions = useKbSuggestions({ subject, body, categoryId, enabled: Boolean(ticketId) })

  const attach = useMutation({
    mutationFn: (articleId: string) => knowledgeApi.attachToTicket(ticketId, articleId),
    onSuccess: async (link) => {
      await queryClient.invalidateQueries({ queryKey: ['tickets', ticketId, 'kb-articles'] })
      toast.success(`${link.number} attached to this ticket`)
    },
  })

  const detach = useMutation({
    mutationFn: (articleId: string) => knowledgeApi.detachFromTicket(ticketId, articleId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['tickets', ticketId, 'kb-articles'] })
      toast.success('Article detached')
    },
  })

  const links = attached.data ?? []
  const attachedIds = new Set(links.map((link) => link.articleId))
  const offered = (suggestions.data ?? []).filter((suggestion) => !attachedIds.has(suggestion.id))

  // Nothing attached and nothing to suggest is most tickets, and a card that says so on every one of them
  // is a card people stop reading — the same call `RelatedProblemsCard` makes.
  if (links.length === 0 && offered.length === 0) return null

  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
      <span className="grid size-10 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><BookOpen size={20} /></span>
      <div>
        <h2 className="font-semibold">Knowledge</h2>
        <p className="mt-0.5 text-sm text-slate-500">
          {links.length > 0
            ? 'The articles this ticket was answered with.'
            : 'Articles that look like they answer this ticket.'}
        </p>
      </div>
    </div>

    {links.length > 0 && <ul className="divide-y divide-slate-100 dark:divide-slate-800">
      {links.map((link) => <li key={link.articleId} className="flex flex-wrap items-center gap-3 p-4">
        <Paperclip size={16} className="shrink-0 text-slate-400" />
        <div className="min-w-0 flex-1">
          <Link to={`/knowledge/${link.articleId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{link.title}</Link>
          <p className="mt-0.5 text-xs text-slate-500">{link.number} · attached by {link.linkedByName} · {formatLocal(link.linkedAt)}</p>
        </div>
        <KbStatusPill status={link.status} />
        <Button
          variant="ghost"
          className="size-9 p-0"
          aria-label={`Detach ${link.number}`}
          disabled={detach.isPending}
          onClick={() => detach.mutate(link.articleId)}>
          <X size={17} />
        </Button>
      </li>)}
    </ul>}

    {offered.length > 0 && <div className="border-t border-slate-200 p-4 dark:border-slate-800">
      <h3 className="text-[13px] font-medium text-slate-500">
        {links.length > 0 ? 'Other articles that may fit' : 'Suggested'}
      </h3>
      <ul className="mt-2 space-y-2">
        {offered.map((suggestion) => <li key={suggestion.id} className="flex flex-wrap items-center gap-3 rounded-lg bg-slate-50 p-3 dark:bg-slate-800/60">
          <div className="min-w-0 flex-1">
            <Link to={`/knowledge/${suggestion.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{suggestion.title}</Link>
            <p className="mt-0.5 truncate text-xs text-slate-500">{suggestion.number} · {suggestion.summary}</p>
          </div>
          <Button variant="secondary" disabled={attach.isPending} onClick={() => attach.mutate(suggestion.id)}>
            <Plus size={16} />Attach
          </Button>
        </li>)}
      </ul>
      {attach.error && <p role="alert" className="mt-2 text-sm text-red-600">{attach.error.message}</p>}
    </div>}

    {detach.error && <p role="alert" className="border-t border-slate-200 p-4 text-sm text-red-600 dark:border-slate-800">{detach.error.message}</p>}
  </section>
}
