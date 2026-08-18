import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRight, CheckCircle2, ClipboardCheck, PackageSearch, TriangleAlert } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { ciTypeLabel } from '../../api/assets'
import {
  reconciliationApi,
  unexpectedReasonLabel,
  type AuditItem,
  type AuditScan,
} from '../../api/reconciliation'
import { Button } from '../../components/ui/Button'
import { ciLifecycleLabel, ciLifecycleTone } from './lifecycle'
import { usePageHeading } from '../../layout/pageHeading'

/**
 * One count, being walked. The scan box is the working half — a wedge scanner types a code and presses
 * Enter — and the three lists below it are the discrepancy report, updating as codes arrive.
 */
export function AuditSessionPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const inputRef = useRef<HTMLInputElement>(null)
  const [code, setCode] = useState('')
  const [confirming, setConfirming] = useState(false)

  const report = useQuery({
    queryKey: ['audit-session', id],
    queryFn: () => reconciliationApi.getAuditReport(id),
    enabled: id !== '',
  })
  usePageHeading(report.data ? { title: report.data.session.name } : null)

  // A wedge scanner fires its code and its Enter at whatever moment the trigger is pulled, so the
  // field has to hold focus before anybody thinks to tap it — the same rule WP-2.7's scan page follows.
  useEffect(() => { inputRef.current?.focus() }, [report.data?.session.status])

  const scan = useMutation({
    mutationFn: (scanned: string) => reconciliationApi.recordAuditScan(id, { code: scanned }),
    onSuccess: async (result) => {
      setCode('')
      await queryClient.invalidateQueries({ queryKey: ['audit-session', id] })
      toast[result.expected ? 'success' : 'warning'](describeScan(result))
      inputRef.current?.focus()
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const close = useMutation({
    mutationFn: () => reconciliationApi.closeAuditSession(id),
    onSuccess: async (session) => {
      await queryClient.invalidateQueries({ queryKey: ['audit-session', id] })
      await queryClient.invalidateQueries({ queryKey: ['audit-sessions'] })
      setConfirming(false)
      toast.success(session.unscannedCount === 0
        ? 'Count closed — everything expected was found.'
        : `Count closed with ${session.unscannedCount} asset${session.unscannedCount === 1 ? '' : 's'} unaccounted for.`)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (report.isLoading) {
    return <div aria-label="Loading count" className="space-y-4">
      {[0, 1, 2].map((index) => <div key={index} className="h-28 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />)}
    </div>
  }

  if (report.isError || !report.data) {
    const notFound = report.error instanceof ApiError && report.error.status === 404
    return <div role="alert" className="grid min-h-64 place-items-center text-center"><div>
      <h1 className="text-xl font-semibold">{notFound ? 'That count does not exist' : 'The count could not be loaded'}</h1>
      <p className="mt-1 text-sm text-slate-500">
        {notFound ? 'It may have been removed since this link was made.' : (report.error as Error | null)?.message}
      </p>
      <Button className="mt-4" variant="secondary" onClick={() => void report.refetch()}>Try again</Button>
    </div></div>
  }

  const { session, scanned, unscanned, unexpected, truncated } = report.data
  const open = session.status === 'Open'

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <Link to="/audits" className="text-[13px] text-slate-500 hover:text-blue-600">Physical audits</Link>
        <h1 className="text-[28px] font-bold">{session.name}</h1>
        <p className="mt-1 text-sm text-slate-500">
          {session.siteName ?? 'The whole estate'} · opened by {session.openedBy} on {new Date(session.openedAt).toLocaleString()}
          {session.closedAt && ` · closed by ${session.closedBy} on ${new Date(session.closedAt).toLocaleString()}`}
        </p>
      </div>
      {open && <div className="flex flex-wrap gap-2 sm:ml-auto">
        {/* Closing is irreversible — a count somebody can top up next week counted nothing on the day
            — so it asks first, per DESIGN.md §6. */}
        {confirming
          ? <>
              <Button disabled={close.isPending} onClick={() => close.mutate()}>
                {close.isPending ? 'Closing…' : `Confirm — close with ${session.unscannedCount} unaccounted for`}
              </Button>
              <Button variant="secondary" onClick={() => setConfirming(false)}>Keep counting</Button>
            </>
          : <Button variant="secondary" onClick={() => setConfirming(true)}><CheckCircle2 size={18} />Close the count</Button>}
      </div>}
    </div>

    <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <Kpi label="Expected" value={session.expectedCount} tone="text-slate-600 bg-slate-100 dark:bg-slate-500/15" icon={<ClipboardCheck size={20} />} />
      <Kpi label="Confirmed" value={session.scannedCount} tone="text-green-600 bg-green-50 dark:bg-green-500/15" icon={<CheckCircle2 size={20} />} />
      <Kpi label="Not found" value={session.unscannedCount} tone="text-red-600 bg-red-50 dark:bg-red-500/15" icon={<PackageSearch size={20} />} />
      <Kpi label="Unexpected" value={session.unexpectedCount} tone="text-amber-600 bg-amber-50 dark:bg-amber-500/15" icon={<TriangleAlert size={20} />} />
    </div>

    {open
      ? <form className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"
          onSubmit={(event) => { event.preventDefault(); if (code.trim()) scan.mutate(code.trim()) }}>
          <label htmlFor="audit-scan" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">
            Asset tag, serial number, or scanned code
          </label>
          <div className="flex gap-2">
            <input id="audit-scan" ref={inputRef} value={code} autoComplete="off" autoCapitalize="off" autoCorrect="off"
              spellCheck={false} enterKeyHint="go" placeholder="NET-0002"
              className="input h-12 flex-1 text-base"
              onChange={(event) => { setCode(event.target.value); if (scan.isError) scan.reset() }} />
            <Button type="submit" className="h-12 px-5" disabled={!code.trim() || scan.isPending}>
              {scan.isPending ? 'Confirming…' : <><ArrowRight size={18} />Confirm</>}
            </Button>
          </div>
          {scan.isError && <p role="alert" className="mt-2 text-xs text-red-600">{scan.error.message}</p>}
        </form>
      : <p className="rounded-xl border border-slate-200 bg-white p-5 text-sm text-slate-500 dark:border-slate-800 dark:bg-slate-900">
          This count is closed. Its lists are what was found on the day — start another count to walk the site again.
        </p>}

    <ItemSection
      title="Not found"
      description="Recorded here and nobody confirmed it. This is the finding a stock take exists to produce."
      items={unscanned}
      emptyMessage="Everything the count expected has been confirmed." />

    {unexpected.length > 0 && <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <header className="border-b border-slate-200 p-5 dark:border-slate-800">
        <h2 className="font-semibold">Found but not expected</h2>
        <p className="mt-1 text-sm text-slate-500">
          Scanned here while the CMDB says otherwise — an asset that moved, or one recorded as disposed
          and still in the building.
        </p>
      </header>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[800px] text-left text-sm">
          <thead><tr>
            {['Asset', 'Asset tag', 'Recorded site', 'Lifecycle', 'Why', 'Confirmed'].map((header) =>
              <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
          </tr></thead>
          <tbody>
            {unexpected.map((item) => <tr key={item.ciId} className="border-t border-slate-200 dark:border-slate-800">
              <td className="h-12 px-4">
                <Link to={`/assets/${item.ciId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{item.name}</Link>
                <span className="ml-2 text-xs text-slate-500">{ciTypeLabel(item.type)}</span>
              </td>
              <td className="h-12 px-4 font-mono text-[13px] text-slate-600 dark:text-slate-300">{item.assetTag ?? '—'}</td>
              <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{item.siteName ?? '—'}</td>
              <td className="h-12 px-4">
                <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(item.lifecycleState)}`}>{ciLifecycleLabel(item.lifecycleState)}</span>
              </td>
              <td className="h-12 px-4 text-amber-700 dark:text-amber-400">{unexpectedReasonLabel(item.reason)}</td>
              <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{new Date(item.scannedAt).toLocaleString()}</td>
            </tr>)}
          </tbody>
        </table>
      </div>
    </section>}

    <ItemSection
      title="Confirmed"
      description="Scanned during this count and recorded where it was found."
      items={scanned}
      emptyMessage="Nothing has been scanned into this count yet." />

    {truncated && <p className="text-[13px] text-slate-500">
      This site holds more assets than the report lists. The counts above are whole; the tables are cut.
    </p>}
  </div>
}

function describeScan(scan: AuditScan) {
  if (scan.expected) {
    return scan.alreadyScanned
      ? `${scan.ciName} had already been confirmed in this count.`
      : `${scan.ciName} confirmed.`
  }

  return `${scan.ciName} confirmed — but ${unexpectedReasonLabel(scan.unexpectedReason ?? 'DifferentSite').toLowerCase()}.`
}

function ItemSection({ title, description, items, emptyMessage }: {
  title: string
  description: string
  items: AuditItem[]
  emptyMessage: string
}) {
  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <header className="border-b border-slate-200 p-5 dark:border-slate-800">
      <h2 className="font-semibold">{title} <span className="ml-1 text-sm font-normal text-slate-500">{items.length}</span></h2>
      <p className="mt-1 text-sm text-slate-500">{description}</p>
    </header>
    {items.length === 0
      ? <p className="p-5 text-sm text-slate-500">{emptyMessage}</p>
      : <div className="overflow-x-auto">
          <table className="w-full min-w-[800px] text-left text-sm">
            <thead><tr>
              {['Asset', 'Asset tag', 'Serial', 'Lifecycle', 'Held by', 'Confirmed'].map((header) =>
                <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
            </tr></thead>
            <tbody>
              {items.map((item) => <tr key={item.ciId} className="border-t border-slate-200 dark:border-slate-800">
                <td className="h-12 px-4">
                  <Link to={`/assets/${item.ciId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{item.name}</Link>
                  <span className="ml-2 text-xs text-slate-500">{ciTypeLabel(item.type)}</span>
                </td>
                <td className="h-12 px-4 font-mono text-[13px] text-slate-600 dark:text-slate-300">{item.assetTag ?? '—'}</td>
                <td className="h-12 px-4 font-mono text-[13px] text-slate-600 dark:text-slate-300">{item.serialNumber ?? '—'}</td>
                <td className="h-12 px-4">
                  <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(item.lifecycleState)}`}>{ciLifecycleLabel(item.lifecycleState)}</span>
                </td>
                <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{item.ownerName ?? <span className="text-slate-400">Nobody</span>}</td>
                <td className="h-12 px-4 text-slate-600 dark:text-slate-300">
                  {item.scannedAt ? `${new Date(item.scannedAt).toLocaleString()} · ${item.scannedBy}` : <span className="text-slate-400">—</span>}
                </td>
              </tr>)}
            </tbody>
          </table>
        </div>}
  </section>
}

function Kpi({ label, value, tone, icon }: { label: string; value: number; tone: string; icon: ReactNode }) {
  return <div className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <span className={`grid size-10 place-items-center rounded-full ${tone}`}>{icon}</span>
    <p className="mt-3 text-[13px] text-slate-500">{label}</p>
    <p className="mt-1 text-3xl font-bold tabular-nums">{value}</p>
  </div>
}
