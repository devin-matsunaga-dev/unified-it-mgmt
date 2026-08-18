import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, BookOpen, Link2, Link2Off, Plus, Search, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { helpdeskApi } from '../../api/helpdesk'
import { problemsApi, type KnowledgeDraft, type ProblemStatus } from '../../api/problems'
import { Button } from '../../components/ui/Button'
import { PriorityPill, StatusPill, formatLocal } from '../tickets/ticketUi'
import { KnowledgeDraftDialog } from './KnowledgeDraftDialog'
import { ProblemStatusPill, problemNextStatuses, problemStatusLabel, subjectHref, subjectLabel } from './problemUi'
import { usePageHeading } from '../../layout/pageHeading'

/**
 * One problem: what it is about, the incidents it explains, and the known-error fields that decide
 * whether anybody else can benefit from it.
 *
 * The two halves of a known error — root cause and workaround — sit above the workflow buttons on
 * purpose. The transition to `KnownError` is refused without them, and a form that put the condition
 * below the button that fails on it would be one people learn by being told no.
 */
export function ProblemDetailPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<KnowledgeDraft | null>(null)
  const [resolution, setResolution] = useState('')
  const [rootCause, setRootCause] = useState('')
  const [workaround, setWorkaround] = useState('')
  const [linking, setLinking] = useState(false)

  const problem = useQuery({ queryKey: ['problems', id], queryFn: () => problemsApi.get(id), enabled: Boolean(id) })
  usePageHeading(problem.data ? { title: problem.data.title } : null)
  const item = problem.data

  // Seeded from the server once, then owned by the person typing — the same rule WP-5.5 applied to the
  // filters it teaches from a URL: a field that kept re-applying itself would undo the first edit.
  useEffect(() => {
    if (!item) return
    setRootCause(item.rootCause ?? '')
    setWorkaround(item.workaround ?? '')
    setResolution(item.resolution ?? '')
    // Keyed on the id alone, deliberately: re-seeding whenever the object identity changed would wipe
    // what somebody is halfway through typing every time the query refetched.
  }, [item?.id])

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['problems'] })
    await queryClient.invalidateQueries({ queryKey: ['problem-suggestions'] })
  }

  const save = useMutation({
    mutationFn: () => problemsApi.update(id, {
      title: item!.title,
      description: item!.description,
      priority: item!.priority,
      ciId: item!.subject?.scope === 'Ci' ? item!.subject.id : null,
      categoryId: item!.subject?.scope === 'Category' ? item!.subject.id : null,
      rootCause: rootCause.trim() || null,
      workaround: workaround.trim() || null,
      assignedTechnicianId: item!.assignedTechnicianId,
    }),
    onSuccess: async () => { await refresh(); toast.success('Cause and workaround saved') },
  })

  const transition = useMutation({
    mutationFn: (target: ProblemStatus) => problemsApi.transition(id, target, resolution.trim() || null),
    onSuccess: async (result) => {
      await refresh()
      toast.success(`Problem is now ${problemStatusLabel(result.problem.status).toLowerCase()}`)
      // Present only on a close. The prompt rides on the response so it cannot be lost to a second
      // request that fails.
      if (result.knowledgeDraft) setDraft(result.knowledgeDraft)
    },
  })

  const unlink = useMutation({
    mutationFn: (ticketId: string) => problemsApi.unlinkIncident(id, ticketId),
    onSuccess: async () => { await refresh(); toast.success('Incident unlinked') },
  })

  if (problem.isPending) return <div aria-label="Loading problem" className="h-64 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
  if (problem.isError || !item) return <p role="alert" className="text-sm text-red-600">This problem could not be loaded.</p>

  const nextStatuses = problemNextStatuses(item.status)
  const needsResolution = (target: ProblemStatus) => target === 'Resolved' || target === 'Closed'
  const canReachKnownError = rootCause.trim().length > 0 && workaround.trim().length > 0
  const unsaved = rootCause.trim() !== (item.rootCause ?? '') || workaround.trim() !== (item.workaround ?? '')

  return <div className="space-y-6">
    <div>
      <Link to="/problems" className="inline-flex items-center gap-1 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={16} />All problems</Link>
      <div className="mt-2 flex flex-wrap items-center gap-3">
        <h1 className="text-[28px] font-bold">{item.title}</h1>
        <ProblemStatusPill status={item.status} />
        <PriorityPill priority={item.priority} />
      </div>
      <p className="mt-1 text-sm text-slate-500">
        {item.number} · opened by {item.openedByName} on {formatLocal(item.createdAt)}
        {item.subject && <> · about <Link to={subjectHref(item.subject)} className="text-blue-600 hover:underline">{subjectLabel(item.subject)}</Link></>}
      </p>
    </div>

    <div className="grid gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
      <div className="space-y-6">
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">What is happening</h2>
          <p className="mt-3 whitespace-pre-wrap text-sm leading-6 text-slate-600 dark:text-slate-300">{item.description}</p>
        </section>

        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-center gap-3">
            <span className="grid size-10 place-items-center rounded-full bg-amber-50 text-amber-600 dark:bg-amber-500/15"><BookOpen size={20} /></span>
            <div>
              <h2 className="font-semibold">Known error</h2>
              <p className="mt-0.5 text-sm text-slate-500">
                Both halves are needed before this becomes a known error — a cause with no workaround helps
                nobody holding a fresh incident.
              </p>
            </div>
          </div>
          <label className="mt-4 block">
            <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Root cause</span>
            <textarea className="input min-h-20 resize-y" value={rootCause} onChange={(event) => setRootCause(event.target.value)} placeholder="What is actually wrong" />
          </label>
          <label className="mt-3 block">
            <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Workaround</span>
            <textarea className="input min-h-20 resize-y" value={workaround} onChange={(event) => setWorkaround(event.target.value)} placeholder="What to do about it until it is fixed" />
          </label>
          {save.error && <p role="alert" className="mt-3 text-sm text-red-600">{save.error.message}</p>}
          <div className="mt-3 flex items-center gap-3">
            <Button variant="secondary" disabled={!unsaved || save.isPending} onClick={() => save.mutate()}>
              {save.isPending ? 'Saving…' : 'Save'}
            </Button>
            {item.isKnownError && !unsaved && <span className="text-[13px] text-slate-500">Published as a known error since {formatLocal(item.knownErrorAt!)}</span>}
            {unsaved && <span className="text-[13px] text-amber-700">Unsaved changes</span>}
          </div>
        </section>

        <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
            <div>
              <h2 className="font-semibold">Incidents</h2>
              <p className="mt-0.5 text-sm text-slate-500">{item.incidentCount === 0 ? 'Nothing is linked yet.' : `${item.incidentCount} incident${item.incidentCount === 1 ? '' : 's'} this problem explains.`}</p>
            </div>
            <Button className="ml-auto" variant="secondary" onClick={() => setLinking((current) => !current)}>
              {linking ? <><X size={16} />Cancel</> : <><Plus size={16} />Link an incident</>}
            </Button>
          </div>
          {linking && <IncidentPicker problemId={id} onLinked={async () => { setLinking(false); await refresh() }} />}
          {(item.incidents ?? []).length === 0
            ? <p className="p-5 text-sm text-slate-500">
                A problem with no incidents is a hunch. Link the tickets it explains so the pattern is
                recorded — and so closing it can write them up.
              </p>
            : <ul className="divide-y divide-slate-200 dark:divide-slate-800">
                {item.incidents!.map((incident) => <li key={incident.ticketId} className="flex flex-wrap items-center gap-x-3 gap-y-2 p-4">
                  <Link to={`/tickets/${incident.ticketId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{incident.title}</Link>
                  <span className="text-xs text-slate-500">{incident.number}</span>
                  <StatusPill status={incident.status} />
                  <PriorityPill priority={incident.priority} />
                  <span className="ml-auto text-xs text-slate-500">Raised {formatLocal(incident.createdAt)}</span>
                  <Button variant="ghost" aria-label={`Unlink ${incident.number}`} disabled={unlink.isPending} onClick={() => unlink.mutate(incident.ticketId)}>
                    <Link2Off size={16} />
                  </Button>
                </li>)}
              </ul>}
        </section>
      </div>

      <div className="space-y-6">
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Workflow</h2>
          <div className="mt-4 grid gap-2">
            {nextStatuses.map((target) => {
              const blockedByFields = target === 'KnownError' && !canReachKnownError
              const blockedByResolution = needsResolution(target) && !resolution.trim()
              return <Button
                key={target}
                variant="secondary"
                disabled={transition.isPending || blockedByFields || blockedByResolution || unsaved}
                onClick={() => transition.mutate(target)}
              >
                {target === 'Investigating' && item.status !== 'Investigating' ? 'Reopen — back to investigating' : problemStatusLabel(target)}
              </Button>
            })}
          </div>
          {unsaved && <p className="mt-3 text-xs text-amber-700">Save the cause and workaround before moving this problem on.</p>}
          {nextStatuses.some(needsResolution) && <>
            {/* The hint sits outside the label on purpose: text inside one becomes part of the field's
                accessible name, so "Resolution" would be read out as a whole sentence. */}
            <label className="mt-4 block">
              <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Resolution</span>
              <textarea className="input min-h-20 resize-y" value={resolution} onChange={(event) => setResolution(event.target.value)} placeholder="What was done about it" />
            </label>
            <p className="mt-1 text-xs text-slate-500">Required to resolve or close.</p>
          </>}
          {transition.error && <p role="alert" className="mt-3 text-sm text-red-600">{transition.error.message}</p>}
        </section>

        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Details</h2>
          <dl className="mt-4 space-y-3 text-sm">
            <Detail label="About" value={subjectLabel(item.subject)} />
            <Detail label="Assigned" value={item.assignedTechnicianId ?? 'Unassigned'} />
            <Detail label="Updated" value={formatLocal(item.updatedAt)} />
            {item.knownErrorAt && <Detail label="Known error since" value={formatLocal(item.knownErrorAt)} />}
            {item.resolvedAt && <Detail label="Resolved" value={formatLocal(item.resolvedAt)} />}
            {item.closedAt && <Detail label="Closed" value={formatLocal(item.closedAt)} />}
          </dl>
        </section>
      </div>
    </div>

    {draft && <KnowledgeDraftDialog draft={draft} onClose={() => setDraft(null)} />}
  </div>
}

/**
 * Finds an incident by what somebody would type — its number or its title. Only incidents can be linked,
 * so the picker asks the ticket list for incidents and the server refuses anything else regardless.
 */
function IncidentPicker({ problemId, onLinked }: { problemId: string; onLinked: () => Promise<void> }) {
  const [search, setSearch] = useState('')
  const results = useQuery({
    queryKey: ['tickets', { problemPicker: search }],
    queryFn: () => helpdeskApi.listTickets({ search, type: 'Incident' }),
    enabled: search.trim().length > 1,
  })
  const link = useMutation({
    mutationFn: (ticketId: string) => problemsApi.linkIncident(problemId, ticketId),
    onSuccess: async () => { setSearch(''); await onLinked(); toast.success('Incident linked') },
  })

  return <div className="border-b border-slate-200 p-4 dark:border-slate-800">
    <label className="relative block">
      <span className="sr-only">Search incidents</span>
      <Search size={16} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
      <input className="input pl-9" autoFocus placeholder="Search incidents by title or number" value={search} onChange={(event) => setSearch(event.target.value)} />
    </label>
    {link.error && <p role="alert" className="mt-2 text-sm text-red-600">{link.error.message}</p>}
    {results.data && <ul className="mt-3 space-y-1">
      {results.data.items.length === 0
        ? <li className="text-sm text-slate-500">No incidents match that.</li>
        : results.data.items.slice(0, 10).map((ticket) => <li key={ticket.id} className="flex items-center gap-2 text-sm">
            <span className="flex-1 truncate">{ticket.title}</span>
            <span className="text-xs text-slate-500">{ticket.number}</span>
            <Button variant="ghost" aria-label={`Link ${ticket.number}`} disabled={link.isPending} onClick={() => link.mutate(ticket.id)}>
              <Link2 size={16} />
            </Button>
          </li>)}
    </ul>}
  </div>
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div className="flex justify-between gap-3">
    <dt className="text-slate-500">{label}</dt>
    <dd className="text-right font-medium text-slate-900 dark:text-slate-100">{value}</dd>
  </div>
}
