import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Check, Clock3, MessageSquare, Send, UserRound } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { helpdeskApi, type Assignment, type Comment, type Transition } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { TicketKnowledgeCard } from '../knowledge/TicketKnowledgeCard'
import { CannedResponsePicker } from './CannedResponsePicker'
import { LinkedAssetsCard } from './LinkedAssetsCard'
import { RelatedProblemsCard } from './RelatedProblemsCard'
import { PriorityPill, StatusPill, displayStatus, formatLocal, formatRemaining, slaIcon, ticketStatuses } from './ticketUi'
import { usePageHeading } from '../../layout/pageHeading'

const workflow = ticketStatuses
type TimelineItem = { id: string; at: string; kind: 'comment' | 'transition' | 'assignment'; internal?: boolean; actor: string; title: string; detail?: string }

export function TicketDetailPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const [now, setNow] = useState(Date.now())
  const [comment, setComment] = useState('')
  const [internal, setInternal] = useState(false)
  const [technicianId, setTechnicianId] = useState('')
  const [resolutionNote, setResolutionNote] = useState('')
  const ticket = useQuery({ queryKey: ['tickets', id], queryFn: () => helpdeskApi.getTicket(id), enabled: Boolean(id) })
  const queues = useQuery({ queryKey: ['queues'], queryFn: helpdeskApi.listQueues })
  usePageHeading(ticket.data ? { title: ticket.data.title } : null)
  const related = useQueries({ queries: [
    { queryKey: ['tickets', id, 'comments'], queryFn: () => helpdeskApi.getComments(id), enabled: Boolean(id) },
    { queryKey: ['tickets', id, 'transitions'], queryFn: () => helpdeskApi.getTransitions(id), enabled: Boolean(id) },
    { queryKey: ['tickets', id, 'assignments'], queryFn: () => helpdeskApi.getAssignments(id), enabled: Boolean(id) },
    { queryKey: ['tickets', id, 'eligible-technicians'], queryFn: () => helpdeskApi.getEligibleTechnicians(id), enabled: Boolean(id && ticket.data?.queueId), retry: false, meta: { suppressErrorToast: true } },
    { queryKey: ['tickets', id, 'sla'], queryFn: () => helpdeskApi.getSla(id), enabled: Boolean(id), retry: false, meta: { suppressErrorToast: true } },
  ] })
  const [comments, transitions, assignments, eligibleTechnicians, sla] = related
  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(timer) }, [])
  const refresh = async () => { await queryClient.invalidateQueries({ queryKey: ['tickets', id] }); await queryClient.invalidateQueries({ queryKey: ['tickets'] }) }
  const transition = useMutation({ mutationFn: (targetStatus: string) => helpdeskApi.transition(id, targetStatus, targetStatus === 'Resolved' ? resolutionNote : null), onSuccess: async () => { setResolutionNote(''); await refresh(); toast.success('Ticket status updated') } })
  const addComment = useMutation({ mutationFn: () => helpdeskApi.addComment(id, comment, internal), onSuccess: async () => { setComment(''); setInternal(false); await queryClient.invalidateQueries({ queryKey: ['tickets', id, 'comments'] }); toast.success(internal ? 'Internal note added' : 'Reply added') } })
  const assign = useMutation({ mutationFn: () => helpdeskApi.assign(id, technicianId), onSuccess: async () => { setTechnicianId(''); await refresh(); toast.success('Ticket assigned') } })
  const placeInQueue = useMutation({ mutationFn: (queueId: string) => helpdeskApi.placeInQueue(id, queueId), onSuccess: async () => { setTechnicianId(''); await refresh(); await queryClient.invalidateQueries({ queryKey: ['tickets', id, 'eligible-technicians'] }); await queryClient.invalidateQueries({ queryKey: ['tickets', id, 'assignments'] }); toast.success('Ticket queue updated') } })
  const timeline = useMemo(() => buildTimeline(comments.data ?? [], transitions.data ?? [], assignments.data ?? []), [comments.data, transitions.data, assignments.data])

  if (ticket.isLoading) return <DetailSkeleton />
  if (ticket.isError || !ticket.data) return <div role="alert" className="rounded-xl border border-red-200 bg-white p-8 text-center dark:bg-slate-900"><h1 className="font-semibold">Ticket could not be loaded</h1><p className="mt-2 text-sm text-slate-500">{ticket.error instanceof Error ? ticket.error.message : 'The ticket may not exist.'}</p><Button className="mt-4" variant="secondary" onClick={() => void ticket.refetch()}>Try again</Button></div>
  const item = ticket.data
  const currentIndex = workflow.indexOf(item.status as typeof workflow[number])
  const allowedTarget = currentIndex >= 0 && currentIndex < workflow.length - 1 ? workflow[currentIndex + 1] : null
  const slaData = sla.data
  const elapsedSinceFetch = slaData ? Math.max(0, (now - sla.dataUpdatedAt) / 1000) : 0
  const remaining = slaData ? slaData.resolutionRemainingSeconds - (slaData.isPaused || slaData.resolutionCompletedAt ? 0 : elapsedSinceFetch) : 0
  const SlaIcon = slaIcon(slaData?.isPaused ?? false, remaining)

  return <div className="space-y-6">
    <Link to="/tickets" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to tickets</Link>
    <header className="flex flex-col gap-4 xl:flex-row xl:items-start"><div className="min-w-0"><div className="flex flex-wrap items-center gap-2"><span className="font-mono text-sm text-slate-500">#{item.number}</span><StatusPill status={item.status} /><PriorityPill priority={item.priority} /></div><h1 className="mt-2 text-[28px] font-bold leading-tight">{item.title}</h1><p className="mt-2 text-sm text-slate-500">Opened {formatLocal(item.createdAt)} by {item.requesterName}</p></div>
      {slaData && <div className={cn('xl:ml-auto flex min-w-56 items-center gap-3 rounded-xl border p-4', remaining < 0 ? 'border-red-200 bg-red-50 text-red-700 dark:border-red-900 dark:bg-red-950/40 dark:text-red-300' : slaData.isPaused ? 'border-amber-200 bg-amber-50 text-amber-700 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-300' : 'border-blue-200 bg-blue-50 text-blue-700 dark:border-blue-900 dark:bg-blue-950/40 dark:text-blue-300')}><SlaIcon size={22} /><div><p className="text-xs font-medium">{slaData.isPaused ? 'SLA paused' : slaData.resolutionCompletedAt ? 'Resolution SLA complete' : 'Resolution SLA'}</p><p className="mt-0.5 font-bold tabular-nums" aria-live="polite">{slaData.resolutionCompletedAt ? 'Completed' : formatRemaining(remaining)}</p></div></div>}
    </header>
    <div className="grid gap-6 xl:grid-cols-[minmax(0,2fr)_minmax(300px,1fr)]">
      <div className="space-y-6">
        {/* WP-5.7: above the description, because "this is not an isolated fault, and here is the
            workaround" changes what the technician does next before they have read anything else. */}
        <RelatedProblemsCard ticketId={id} />
        {/* WP-5.9: under the problems card and above the description, for the same reason — an answer
            somebody has already written changes what happens next, and the attachment made here is what
            the resolution ends up citing. */}
        <TicketKnowledgeCard ticketId={id} subject={item.title} body={item.description} categoryId={item.categoryId} />
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><h2 className="font-semibold">Description</h2><p className="mt-3 whitespace-pre-wrap text-sm leading-6 text-slate-600 dark:text-slate-300">{item.description}</p></section>
        <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900"><div className="border-b border-slate-200 p-5 dark:border-slate-800"><h2 className="font-semibold">Activity</h2><p className="mt-1 text-sm text-slate-500">Comments, internal notes, status changes, and assignments.</p></div>
          <div className="p-5"><form onSubmit={(event) => { event.preventDefault(); if (comment.trim()) addComment.mutate() }}><CannedResponsePicker ticketId={id} value={comment} onChange={setComment} /><textarea aria-label="Comment" className="input min-h-24 resize-y" placeholder={internal ? 'Add an internal note…' : 'Write a public reply…'} value={comment} onChange={(event) => setComment(event.target.value)} /><div className="mt-3 flex flex-wrap items-center gap-3"><label className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300"><input type="checkbox" checked={internal} onChange={(event) => setInternal(event.target.checked)} />Internal note</label>{addComment.error && <span role="alert" className="text-sm text-red-600">{addComment.error.message}</span>}<Button className="ml-auto" type="submit" disabled={!comment.trim() || addComment.isPending}><Send size={16} />{internal ? 'Add note' : 'Reply'}</Button></div></form></div>
          <ol className="border-t border-slate-200 px-5 pb-5 dark:border-slate-800">{timeline.length === 0 ? <li className="py-8 text-center text-sm text-slate-500">No activity yet.</li> : timeline.map((event) => <li key={`${event.kind}-${event.id}`} className={cn('relative ml-3 border-l border-slate-200 py-4 pl-7 dark:border-slate-700', event.internal && 'my-3 rounded-r-lg border-l-4 border-amber-400 bg-amber-50 pr-4 dark:bg-amber-950/30')}><span className={cn('absolute -left-3 top-4 grid size-6 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800', event.internal && 'bg-amber-100 text-amber-700')}>{event.kind === 'comment' ? <MessageSquare size={13} /> : event.kind === 'assignment' ? <UserRound size={13} /> : <Check size={13} />}</span><div className="flex flex-wrap items-center gap-2"><p className="text-sm font-medium">{event.title}</p>{event.internal && <span className="rounded bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700">Internal note</span>}</div>{event.detail && <p className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-600 dark:text-slate-300">{event.detail}</p>}<p className="mt-2 text-xs text-slate-500">{event.actor} · {formatLocal(event.at)}</p></li>)}</ol>
        </section>
        <LinkedAssetsCard ticketId={id} />
      </div>
      <aside className="space-y-6">
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><h2 className="font-semibold">Workflow</h2><div className="mt-4 grid grid-cols-2 gap-2">{workflow.map((status) => <Button key={status} variant={status === allowedTarget ? 'primary' : 'secondary'} disabled={status !== allowedTarget || transition.isPending || (status === 'Resolved' && !resolutionNote.trim())} onClick={() => transition.mutate(status)}>{status === item.status ? 'Current: ' : ''}{displayStatus(status)}</Button>)}</div>{allowedTarget === 'Resolved' && <label className="mt-4 block"><span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Resolution note</span><textarea className="input min-h-20 resize-y" value={resolutionNote} onChange={(event) => setResolutionNote(event.target.value)} /></label>}{transition.error && <p role="alert" className="mt-3 text-sm text-red-600">{transition.error.message}</p>}<p className="mt-3 text-xs text-slate-500">Unavailable transitions are disabled by the configured default workflow.</p></section>
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><h2 className="font-semibold">Assignment</h2><dl className="mt-4 space-y-3 text-sm"><Detail label="Queue" value={item.queueName ?? 'Unqueued'} /><Detail label="Technician" value={item.assignedTechnicianId ?? 'Unassigned'} /></dl><label className="mt-4 block"><span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Queue</span><select aria-label="Ticket queue" className="input" value={item.queueId ?? ''} disabled={queues.isLoading || placeInQueue.isPending} onChange={(event) => { if (event.target.value && event.target.value !== item.queueId) placeInQueue.mutate(event.target.value) }}><option value="" disabled>Select a queue</option>{queues.data?.map((queue) => <option key={queue.id} value={queue.id}>{queue.name}</option>)}</select></label>{placeInQueue.error && <p role="alert" className="mt-2 text-sm text-red-600">{placeInQueue.error.message}</p>}<form className="mt-4" onSubmit={(event) => { event.preventDefault(); if (technicianId) assign.mutate() }}><label><span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Assign technician</span><select aria-label="Assign technician" className="input" value={technicianId} onChange={(event) => setTechnicianId(event.target.value)} disabled={!item.queueId || eligibleTechnicians.isLoading}><option value="">{eligibleTechnicians.isLoading ? 'Loading technicians…' : 'Select a technician'}</option>{eligibleTechnicians.data?.map((technician) => <option key={technician.id} value={technician.id}>{technician.id}</option>)}</select></label>{assign.error && <p role="alert" className="mt-2 text-sm text-red-600">{assign.error.message}</p>}{eligibleTechnicians.isError && <p role="alert" className="mt-2 text-sm text-red-600">Eligible technicians could not be loaded.</p>}<Button className="mt-3 w-full" variant="secondary" disabled={!technicianId || !item.queueId || assign.isPending}>Assign</Button>{!item.queueId ? <p className="mt-2 text-xs text-amber-700">Select a queue before assigning a technician.</p> : !eligibleTechnicians.isLoading && eligibleTechnicians.data?.length === 0 ? <p className="mt-2 text-xs text-amber-700">This queue's team has no technicians.</p> : null}</form></section>
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><h2 className="font-semibold">Ticket details</h2><dl className="mt-4 space-y-3 text-sm"><Detail label="Category" value={item.categoryName ?? 'Uncategorised'} /><Detail label="Type" value={item.type === 'Incident' ? 'Incident' : 'Service request'} /><Detail label="Urgency" value={item.urgency} /><Detail label="Impact" value={item.impact} /><Detail label="Updated" value={formatLocal(item.updatedAt)} /></dl>{item.customFields.length > 0 && <><h3 className="mt-5 text-[13px] font-medium text-slate-500">{item.categoryName} fields</h3><dl className="mt-3 space-y-3 text-sm">{item.customFields.map((field) => <Detail key={field.fieldId} label={field.label} value={field.value} />)}</dl></>}</section>
      </aside>
    </div>
  </div>
}

function buildTimeline(comments: Comment[], transitions: Transition[], assignments: Assignment[]): TimelineItem[] { return [...comments.map((item): TimelineItem => ({ id: item.id, at: item.createdAt, kind: 'comment', internal: item.isInternal, actor: item.authorName, title: item.isInternal ? 'Internal note added' : 'Public reply added', detail: item.body })), ...transitions.map((item): TimelineItem => ({ id: item.id, at: item.occurredAt, kind: 'transition', actor: item.actorId, title: `Status changed from ${displayStatus(item.fromStatus)} to ${displayStatus(item.toStatus)}`, detail: item.resolutionNote ?? undefined })), ...assignments.map((item): TimelineItem => ({ id: item.id, at: item.occurredAt, kind: 'assignment', actor: item.actorId, title: `Assigned to ${item.toTechnicianId}`, detail: item.fromTechnicianId ? `Previously assigned to ${item.fromTechnicianId}` : undefined }))].sort((a, b) => new Date(b.at).getTime() - new Date(a.at).getTime()) }
function Detail({ label, value }: { label: string; value: string }) { return <div className="flex gap-4"><dt className="text-slate-500">{label}</dt><dd className="ml-auto max-w-[65%] break-words text-right font-medium">{value}</dd></div> }
function DetailSkeleton() { return <div aria-label="Loading ticket" className="space-y-6"><div className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" /><div className="grid gap-6 xl:grid-cols-[2fr_1fr]"><div className="h-96 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" /><div className="h-72 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" /></div></div> }
