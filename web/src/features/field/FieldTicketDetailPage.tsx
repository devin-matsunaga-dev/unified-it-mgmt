import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Box, ChevronLeft, MessageSquarePlus, UserRound } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { helpdeskApi } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { FieldActionBar } from '../../layout/FieldShell'
import { cn } from '../../lib/utils'
import { PriorityPill, StatusPill, displayStatus, formatLocal } from '../tickets/ticketUi'

/**
 * One ticket, sized for a corridor. It answers what a technician standing in front of the problem
 * needs — what is wrong, who reported it, which assets it is about, what has been said — and offers
 * only the three things they actually do there: pick it up, park it, or finish it. Assignment,
 * queues, categories, SLA panels and custom fields stay on the desktop.
 */

/** The moves a technician makes in the field. Everything else in the workflow is desk work. */
const moves = [
  { status: 'InProgress', label: 'Start work' },
  { status: 'Pending', label: 'Park it' },
  { status: 'Resolved', label: 'Resolve' },
] as const

export function FieldTicketDetailPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const [note, setNote] = useState('')
  const [resolving, setResolving] = useState(false)
  const [resolution, setResolution] = useState('')

  const ticket = useQuery({ queryKey: ['ticket', id], queryFn: () => helpdeskApi.getTicket(id), enabled: Boolean(id) })
  const comments = useQuery({ queryKey: ['ticket-comments', id], queryFn: () => helpdeskApi.getComments(id), enabled: Boolean(id) })
  const assets = useQuery({ queryKey: ['ticket-cis', id], queryFn: () => helpdeskApi.getTicketCis(id), enabled: Boolean(id) })

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['ticket', id] })

  const addNote = useMutation({
    // Internal, always. A note typed one-handed at a rack is a work note for whoever picks the ticket
    // up next, not correspondence with the person who raised it.
    mutationFn: () => helpdeskApi.addComment(id, note.trim(), true),
    onSuccess: async () => {
      setNote('')
      await queryClient.invalidateQueries({ queryKey: ['ticket-comments', id] })
      toast.success('Note added')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const move = useMutation({
    // The server requires a resolution note to resolve, so the button opens a field rather than
    // firing a request that would come back 400.
    mutationFn: (target: string) => helpdeskApi.transition(id, target, target === 'Resolved' ? resolution.trim() : null),
    onSuccess: async (updated) => {
      setResolving(false)
      setResolution('')
      await refresh()
      toast.success(`${updated.number} is ${displayStatus(updated.status).toLowerCase()}`)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (ticket.isLoading) {
    return <div aria-label="Loading" className="space-y-3">
      <div className="h-28 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
      <div className="h-40 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
    </div>
  }

  if (ticket.isError || !ticket.data) {
    return <div role="alert" className="rounded-xl border border-slate-200 bg-white p-6 text-center dark:border-slate-800 dark:bg-slate-900">
      <h1 className="text-lg font-semibold">Ticket not found</h1>
      <p className="mt-2 text-[15px] text-slate-500">It may have been closed and removed, or the network dropped on the way.</p>
      <Button className="mt-5 h-12 w-full" variant="secondary" onClick={() => void ticket.refetch()}>Try again</Button>
    </div>
  }

  const item = ticket.data
  const available = moves.filter((option) => option.status !== item.status)

  return <>
    <Link to="/field/tickets" className="inline-flex h-11 items-center gap-1 text-[15px] font-medium text-blue-600">
      <ChevronLeft size={18} />Tickets
    </Link>

    <section className="mt-1 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-center gap-2">
        <span className="text-[13px] tabular-nums text-slate-500">{item.number}</span>
        <span className="ml-auto flex shrink-0 gap-1.5">
          <PriorityPill priority={item.priority} />
          <StatusPill status={item.status} />
        </span>
      </div>
      <h1 className="mt-1 text-[22px] font-bold leading-tight">{item.title}</h1>
      <p className="mt-2 whitespace-pre-wrap text-[15px] text-slate-600 dark:text-slate-300">{item.description}</p>
      <p className="mt-4 flex items-center gap-1.5 text-[15px]">
        <UserRound size={16} className="text-slate-400" />
        <span className="text-slate-500">Raised by</span>
        <span className="font-medium">{item.requesterName}</span>
      </p>
    </section>

    {(assets.data?.length ?? 0) > 0 && <section className="mt-3 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-base font-semibold">Assets</h2>
      <ul className="mt-3 space-y-2">
        {assets.data?.map((link) => <li key={link.id}>
          {/* Straight to the field CI screen: from a ticket, the next thing wanted is the asset itself. */}
          <Link to={`/field/ci/${link.ciId}`} className="flex min-h-12 items-center gap-2 rounded-lg border border-slate-200 px-3 text-[15px] font-medium dark:border-slate-700">
            <Box size={16} className="shrink-0 text-slate-400" />
            <span className="truncate">{link.ciName}</span>
            {link.assetTag && <span className="ml-auto shrink-0 text-[13px] tabular-nums text-slate-500">{link.assetTag}</span>}
          </Link>
        </li>)}
      </ul>
    </section>}

    <section className="mt-3 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-base font-semibold">Notes</h2>
      {comments.data?.length === 0 && <p className="mt-2 text-[15px] text-slate-500">Nothing has been added yet.</p>}
      <ul className="mt-3 space-y-3">
        {comments.data?.map((comment) => <li key={comment.id} className="text-[15px]">
          <p className="text-[13px] text-slate-500">{comment.authorName} · {formatLocal(comment.createdAt)}</p>
          <p className="mt-0.5 whitespace-pre-wrap">{comment.body}</p>
        </li>)}
      </ul>
      <form
        className="mt-4"
        onSubmit={(event) => {
          event.preventDefault()
          if (note.trim()) addNote.mutate()
        }}
      >
        <label htmlFor="field-ticket-note" className="text-[13px] font-medium text-slate-500">Add a work note</label>
        <textarea
          id="field-ticket-note"
          value={note}
          onChange={(event) => setNote(event.target.value)}
          rows={3}
          className="mt-1.5 w-full rounded-lg border border-slate-200 bg-white p-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
        />
        <Button type="submit" variant="secondary" className="mt-2 h-12 w-full text-[15px]" disabled={!note.trim() || addNote.isPending}>
          <MessageSquarePlus size={18} />{addNote.isPending ? 'Adding…' : 'Add note'}
        </Button>
      </form>
    </section>

    {resolving && <section className="mt-3 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <label htmlFor="field-ticket-resolution" className="text-[13px] font-medium text-slate-500">What fixed it?</label>
      <textarea
        id="field-ticket-resolution"
        value={resolution}
        onChange={(event) => setResolution(event.target.value)}
        rows={3}
        className="mt-1.5 w-full rounded-lg border border-slate-200 bg-white p-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
      />
      <p className="mt-1.5 text-[13px] text-slate-500">A resolution note is required before a ticket can be resolved.</p>
    </section>}

    <FieldActionBar>
      {resolving
        ? <>
            <Button className="h-12 w-full text-[15px]" disabled={!resolution.trim() || move.isPending} onClick={() => move.mutate('Resolved')}>
              {move.isPending ? 'Resolving…' : 'Confirm resolve'}
            </Button>
            <Button variant="secondary" className="h-12 w-full text-[15px]" onClick={() => setResolving(false)}>Cancel</Button>
          </>
        : available.map((option) => <Button
            key={option.status}
            variant={option.status === 'Resolved' ? 'primary' : 'secondary'}
            className={cn('h-12 w-full text-[15px]')}
            disabled={move.isPending}
            onClick={() => option.status === 'Resolved' ? setResolving(true) : move.mutate(option.status)}
          >{option.label}</Button>)}
    </FieldActionBar>
  </>
}
