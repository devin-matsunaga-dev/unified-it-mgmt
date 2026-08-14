import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ClipboardCheck, Plus } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { directoryApi } from '../../api/directory'
import { reconciliationApi, type AuditSessionStatus } from '../../api/reconciliation'
import { Button } from '../../components/ui/Button'

/**
 * The stock takes: one row per count somebody has walked, open ones first.
 *
 * The list deliberately carries the number of scans rather than how many assets are still owed —
 * working that out means classifying every CI in the session's scope, and the answer belongs on the
 * session's own page, which is where somebody is asking.
 */
export function AuditSessionsPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<AuditSessionStatus | ''>('')
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [siteId, setSiteId] = useState('')
  const [note, setNote] = useState('')

  const sites = useQuery({ queryKey: ['directory-sites'], queryFn: () => directoryApi.listSites() })
  const sessions = useQuery({
    queryKey: ['audit-sessions', status],
    queryFn: () => reconciliationApi.listAuditSessions(status || undefined),
  })

  const create = useMutation({
    mutationFn: () => reconciliationApi.createAuditSession({
      name: name.trim(),
      siteId: siteId || null,
      note: note.trim() || null,
    }),
    onSuccess: async (session) => {
      await queryClient.invalidateQueries({ queryKey: ['audit-sessions'] })
      setCreating(false)
      setName('')
      setNote('')
      navigate(`/audits/${session.id}`)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const nameError = create.error instanceof ApiError ? create.error.errors?.name?.[0] : undefined

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[28px] font-bold">Physical audits</h1>
        <p className="mt-1 text-sm text-slate-500">
          Walk a site with a scanner and confirm what is actually there. The finding is what does not
          turn up, so a count only means something against the list it set out to walk.
        </p>
      </div>
      <div className="flex flex-wrap gap-2 sm:ml-auto">
        <Button variant="secondary" onClick={() => navigate('/assets/drift')}>Drift report</Button>
        <Button onClick={() => setCreating((open) => !open)}><Plus size={18} />Start a count</Button>
      </div>
    </div>

    {creating && <form
      className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); if (name.trim()) create.mutate() }}>
      <div className="grid gap-4 sm:grid-cols-2">
        <div>
          <label htmlFor="audit-name" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Name</label>
          <input id="audit-name" value={name} onChange={(event) => setName(event.target.value)} className="input"
            placeholder="Q3 data centre count" />
          {nameError && <p role="alert" className="mt-1 text-xs text-red-600">{nameError}</p>}
        </div>
        <div>
          <label htmlFor="audit-site" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Site</label>
          <select id="audit-site" className="input" value={siteId} onChange={(event) => setSiteId(event.target.value)}>
            {/* Estate-wide is a real choice rather than a missing one: a small organisation counts
                everything in an afternoon and has nothing to gain from three sessions. */}
            <option value="">The whole estate</option>
            {(sites.data ?? []).map((site) => <option key={site.id} value={site.id}>{site.name}</option>)}
          </select>
        </div>
        <div className="sm:col-span-2">
          <label htmlFor="audit-note" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Note</label>
          <input id="audit-note" value={note} onChange={(event) => setNote(event.target.value)} className="input"
            placeholder="Why this count is being run" />
        </div>
      </div>
      <div className="mt-4 flex gap-2">
        <Button type="submit" disabled={!name.trim() || create.isPending}>{create.isPending ? 'Starting…' : 'Start counting'}</Button>
        <Button type="button" variant="secondary" onClick={() => setCreating(false)}>Cancel</Button>
      </div>
    </form>}

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <select aria-label="Filter by status" className="input w-auto min-w-40" value={status}
          onChange={(event) => setStatus(event.target.value as AuditSessionStatus | '')}>
          <option value="">Every count</option>
          <option value="Open">Open</option>
          <option value="Closed">Closed</option>
        </select>
      </div>

      {sessions.isLoading ? <TableSkeleton />
        : sessions.isError ? <ErrorState error={sessions.error} retry={() => void sessions.refetch()} />
        : (sessions.data?.items.length ?? 0) === 0 ? <EmptyState onStart={() => setCreating(true)} />
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[800px] text-left text-sm">
              <thead><tr>
                {['Count', 'Scope', 'Status', 'Confirmed', 'Opened', 'Closed'].map((header) =>
                  <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {sessions.data!.items.map((session) => <tr key={session.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4">
                    <Link to={`/audits/${session.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{session.name}</Link>
                  </td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{session.siteName ?? 'The whole estate'}</td>
                  <td className="h-12 px-4">
                    <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${session.status === 'Open' ? 'bg-blue-50 text-blue-600 dark:bg-blue-500/15' : 'bg-slate-100 text-slate-600 dark:bg-slate-500/15'}`}>
                      {session.status === 'Open' ? 'Open' : 'Closed'}
                    </span>
                  </td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{session.scanCount}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{new Date(session.openedAt).toLocaleString()} · {session.openedBy}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{session.closedAt ? new Date(session.closedAt).toLocaleString() : <span className="text-slate-400">—</span>}</td>
                </tr>)}
              </tbody>
            </table>
          </div>}
    </section>
  </div>
}

function EmptyState({ onStart }: { onStart: () => void }) {
  return <div className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><ClipboardCheck /></span>
    <h2 className="mt-3 font-semibold">Nobody has counted anything yet</h2>
    <p className="mt-1 text-sm text-slate-500">
      Start a count, walk the site scanning labels, and the report lists whatever did not turn up.
    </p>
    <Button className="mt-4" onClick={onStart}>Start a count</Button>
  </div></div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) {
  return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span>
    <h2 className="mt-3 font-semibold">The counts could not be loaded</h2>
    <p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div></div>
}

function TableSkeleton() {
  return <div aria-label="Loading counts" className="space-y-px p-4">{Array.from({ length: 4 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
}
