import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BookOpen, Check, Lightbulb, Plus, Radar, Search, ShieldQuestion, X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { problemsApi, type ProblemStatus, type ProblemSuggestion } from '../../api/problems'
import { Button } from '../../components/ui/Button'
import { PriorityPill, formatLocal } from '../tickets/ticketUi'
import { ProblemStatusPill, problemStatusLabel, problemStatuses, skipReasonLabel, subjectLabel, windowSummary } from './problemUi'

/**
 * The problem board, the known-error list and the recurrence inbox on one screen.
 *
 * They are one screen rather than three because they are one workflow read in one direction: the platform
 * noticed something, somebody decided whether it is a problem, and what came of it is a known error other
 * people can find. Splitting the inbox onto a page of its own would make the suggestion something you have
 * to go and look for — which is exactly what a suggestion cannot be.
 */
export function ProblemListPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<ProblemStatus | ''>('')
  const [knownErrorsOnly, setKnownErrorsOnly] = useState(false)
  const [creating, setCreating] = useState(false)

  const problems = useQuery({
    queryKey: ['problems', { search, status, knownErrorsOnly }],
    queryFn: () => problemsApi.list({
      search: search || undefined,
      statuses: status ? [status] : undefined,
      knownErrorsOnly,
      pageSize: 100,
    }),
  })
  const suggestions = useQuery({
    queryKey: ['problem-suggestions', 'Open'],
    queryFn: () => problemsApi.listSuggestions('Open'),
  })

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['problems'] })
    await queryClient.invalidateQueries({ queryKey: ['problem-suggestions'] })
  }

  const detect = useMutation({
    mutationFn: () => problemsApi.detect(),
    onSuccess: async (run) => {
      await refresh()
      const skipped = Object.entries(run.skipped)
        .map(([reason, count]) => `${count} ${skipReasonLabel(reason)}`)
        .join(', ')
      toast.success(run.suggested === 0
        ? `Nothing new. ${run.examined} subject${run.examined === 1 ? '' : 's'} examined${skipped ? ` — ${skipped}` : ''}.`
        : `${run.suggested} recurrence${run.suggested === 1 ? '' : 's'} found across ${run.examined} subjects.`)
    },
  })

  const items = problems.data?.items ?? []
  const openSuggestions = suggestions.data ?? []
  const knownErrorCount = items.filter((problem) => problem.isKnownError).length

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[28px] font-bold">Problems</h1>
        <p className="mt-1 text-sm text-slate-500">
          The causes behind repeated incidents, and the known errors people can work around while they are
          being fixed.
        </p>
      </div>
      <div className="flex flex-wrap gap-2 sm:ml-auto">
        <Button variant="secondary" disabled={detect.isPending} onClick={() => detect.mutate()}>
          <Radar size={18} />{detect.isPending ? 'Looking…' : 'Look for recurrences'}
        </Button>
        <Button onClick={() => setCreating(true)}><Plus size={18} />New problem</Button>
      </div>
    </div>

    <div className="grid gap-4 sm:grid-cols-3">
      <Kpi label="Problems shown" value={problems.data?.total} tone="text-blue-600 bg-blue-50 dark:bg-blue-500/15" icon={<ShieldQuestion size={20} />} />
      <Kpi label="Known errors here" value={problems.isSuccess ? knownErrorCount : undefined} tone="text-amber-600 bg-amber-50 dark:bg-amber-500/15" icon={<BookOpen size={20} />} />
      <Kpi label="Recurrences waiting" value={suggestions.isSuccess ? openSuggestions.length : undefined} tone="text-slate-600 bg-slate-100 dark:bg-slate-500/15" icon={<Lightbulb size={20} />} />
    </div>

    <SuggestionInbox suggestions={openSuggestions} loading={suggestions.isPending} onAnswered={refresh} />

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="relative min-w-56 flex-1">
          <span className="sr-only">Search problems</span>
          <Search size={16} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
          <input
            className="input pl-9"
            placeholder="Search titles, causes and workarounds"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
        </label>
        <select aria-label="Filter by status" className="input w-auto min-w-44" value={status} onChange={(event) => setStatus(event.target.value as ProblemStatus | '')}>
          <option value="">Every status</option>
          {problemStatuses.map((option) => <option key={option} value={option}>{problemStatusLabel(option)}</option>)}
        </select>
        <label className="flex items-center gap-2 text-[13px] text-slate-600 dark:text-slate-300">
          <input type="checkbox" className="size-4 rounded border-slate-300" checked={knownErrorsOnly} onChange={(event) => setKnownErrorsOnly(event.target.checked)} />
          Known errors only
        </label>
      </div>

      {problems.isPending ? <TableSkeleton />
        : problems.isError ? <ErrorState error={problems.error} retry={() => void problems.refetch()} />
        : items.length === 0 ? <EmptyState knownErrorsOnly={knownErrorsOnly} filtered={Boolean(search || status)} />
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[900px] text-left text-sm">
              <thead><tr>
                {['Problem', 'About', 'Status', 'Priority', 'Incidents', 'Updated'].map((header) =>
                  <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {items.map((problem) => <tr key={problem.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4">
                    <Link to={`/problems/${problem.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{problem.title}</Link>
                    <span className="ml-2 text-xs text-slate-500">{problem.number}</span>
                  </td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{subjectLabel(problem.subject)}</td>
                  <td className="h-12 px-4"><ProblemStatusPill status={problem.status} /></td>
                  <td className="h-12 px-4"><PriorityPill priority={problem.priority} /></td>
                  <td className="h-12 px-4 text-right tabular-nums text-slate-600 dark:text-slate-300">{problem.incidentCount}</td>
                  <td className="h-12 px-4 text-slate-500">{formatLocal(problem.updatedAt)}</td>
                </tr>)}
              </tbody>
            </table>
          </div>}
    </section>

    {creating && <CreateProblemDialog
      onClose={() => setCreating(false)}
      onCreated={async (id) => { setCreating(false); await refresh(); navigate(`/problems/${id}`) }}
    />}
  </div>
}

/**
 * What the nightly pass noticed and nobody has answered yet.
 *
 * Never hidden when empty: a pane that vanishes reads as a feature that broke, and "nothing is recurring"
 * is a useful thing for a board to say out loud.
 */
function SuggestionInbox({ suggestions, loading, onAnswered }: {
  suggestions: ProblemSuggestion[]
  loading: boolean
  onAnswered: () => Promise<void>
}) {
  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
      <span className="grid size-10 place-items-center rounded-full bg-amber-50 text-amber-600 dark:bg-amber-500/15"><Lightbulb size={20} /></span>
      <div>
        <h2 className="font-semibold">Recurrences worth a look</h2>
        <p className="mt-0.5 text-sm text-slate-500">
          Incidents piling up on one thing. The platform can see they cluster; only you can see whether they
          share a cause.
        </p>
      </div>
    </div>
    {loading
      ? <div aria-label="Loading recurrences" className="space-y-2 p-4">
          {[0, 1].map((index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
        </div>
      : suggestions.length === 0
        ? <p className="p-5 text-sm text-slate-500">
            Nothing is recurring above the threshold. Suggestions appear here on their own once a
            configuration item or a category collects enough incidents in one window.
          </p>
        : <ul className="divide-y divide-slate-200 dark:divide-slate-800">
            {suggestions.map((suggestion) => <SuggestionRow key={suggestion.id} suggestion={suggestion} onAnswered={onAnswered} />)}
          </ul>}
  </section>
}

function SuggestionRow({ suggestion, onAnswered }: { suggestion: ProblemSuggestion; onAnswered: () => Promise<void> }) {
  const navigate = useNavigate()
  const [dismissing, setDismissing] = useState(false)
  const [reason, setReason] = useState('')

  const accept = useMutation({
    mutationFn: () => problemsApi.acceptSuggestion(suggestion.id),
    onSuccess: async (answered) => {
      await onAnswered()
      toast.success(`${answered.createdProblemNumber} opened with its incidents attached`)
      if (answered.createdProblemId) navigate(`/problems/${answered.createdProblemId}`)
    },
  })
  const dismiss = useMutation({
    mutationFn: () => problemsApi.dismissSuggestion(suggestion.id, reason || undefined),
    onSuccess: async () => { setDismissing(false); await onAnswered(); toast.success('Suggestion dismissed') },
  })

  return <li className="p-4">
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
      <span className="font-medium text-slate-900 dark:text-slate-100">{subjectLabel(suggestion.subject)}</span>
      <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
        {suggestion.scope === 'Ci' ? 'Configuration item' : 'Category'}
      </span>
      <span className="text-sm text-slate-500">{windowSummary(suggestion.incidentCount, suggestion.windowStart, suggestion.windowEnd)}</span>
      <div className="ml-auto flex gap-2">
        <Button variant="secondary" disabled={dismiss.isPending || accept.isPending} onClick={() => setDismissing((current) => !current)}>
          <X size={16} />Not one problem
        </Button>
        <Button disabled={accept.isPending || dismiss.isPending} onClick={() => accept.mutate()}>
          <Check size={16} />{accept.isPending ? 'Opening…' : 'Make it a problem'}
        </Button>
      </div>
    </div>
    {dismissing && <form
      className="mt-3 flex flex-wrap items-end gap-2"
      onSubmit={(event) => { event.preventDefault(); dismiss.mutate() }}
    >
      <label className="min-w-56 flex-1">
        <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Why is it not one problem? (optional)</span>
        <input className="input" value={reason} onChange={(event) => setReason(event.target.value)} placeholder="Three unrelated faults that happened to share a rack" />
      </label>
      <Button variant="secondary" disabled={dismiss.isPending}>Dismiss</Button>
      <p className="w-full text-xs text-slate-500">
        Dismissing quiets this subject for a while. If the incidents keep coming after that, it will be
        suggested again.
      </p>
    </form>}
    {(accept.error || dismiss.error) && <p role="alert" className="mt-2 text-sm text-red-600">{(accept.error ?? dismiss.error)?.message}</p>}
  </li>
}

function CreateProblemDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => Promise<void> }) {
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')

  const create = useMutation({
    mutationFn: () => problemsApi.create({ title, description, priority: 'Medium' }),
    onSuccess: (problem) => void onCreated(problem.id),
  })

  return <div role="dialog" aria-modal="true" aria-label="New problem" className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4">
    <form
      className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); create.mutate() }}
    >
      <h2 className="font-semibold">New problem</h2>
      <p className="mt-1 text-sm text-slate-500">
        Open one when you already suspect a cause. If the platform spotted the pattern first, accept its
        suggestion instead — that attaches the incidents for you.
      </p>
      <label className="mt-4 block">
        <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Title</span>
        <input className="input" value={title} onChange={(event) => setTitle(event.target.value)} required maxLength={200} />
      </label>
      <label className="mt-3 block">
        <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">What is happening</span>
        <textarea className="input min-h-24 resize-y" value={description} onChange={(event) => setDescription(event.target.value)} required />
      </label>
      {create.error && <p role="alert" className="mt-3 text-sm text-red-600">{create.error.message}</p>}
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onClose}>Cancel</Button>
        <Button disabled={create.isPending || !title.trim() || !description.trim()}>{create.isPending ? 'Opening…' : 'Open problem'}</Button>
      </div>
    </form>
  </div>
}

function Kpi({ label, value, tone, icon }: { label: string; value: number | undefined; tone: string; icon: ReactNode }) {
  return <div className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <span className={`grid size-10 place-items-center rounded-full ${tone}`}>{icon}</span>
    <p className="mt-3 text-[13px] text-slate-500">{label}</p>
    {/* A failed read reads "Unavailable" rather than 0 — a zero is a claim about the estate (WP-2.11). */}
    <p className="mt-1 text-3xl font-bold tabular-nums">{value ?? <span className="text-base font-medium text-slate-400">Unavailable</span>}</p>
  </div>
}

function EmptyState({ knownErrorsOnly, filtered }: { knownErrorsOnly: boolean; filtered: boolean }) {
  return <div className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15">
      {knownErrorsOnly ? <BookOpen /> : <ShieldQuestion />}
    </span>
    <h2 className="mt-3 font-semibold">
      {knownErrorsOnly ? 'No known errors yet' : filtered ? 'Nothing matches that' : 'No problems have been opened'}
    </h2>
    <p className="mt-1 text-sm text-slate-500">
      {knownErrorsOnly
        ? 'A problem becomes a known error once somebody records both its root cause and a workaround. Until then it is still being investigated.'
        : filtered
          ? 'Try a different status, or clear the search.'
          : 'A problem is the cause behind several incidents. Open one when you suspect a pattern, or wait for a recurrence to be suggested above.'}
    </p>
  </div></div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) {
  return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span>
    <h2 className="mt-3 font-semibold">Problems could not be loaded</h2>
    <p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div></div>
}

function TableSkeleton() {
  return <div aria-label="Loading problems" className="space-y-px p-4">
    {Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
  </div>
}
