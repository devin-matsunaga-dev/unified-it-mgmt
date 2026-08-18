import { useQuery } from '@tanstack/react-query'
import { ArrowRight, Cable, GitCompareArrows, Radar, ScanSearch } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { ciTypeLabel } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import {
  driftKindLabel,
  driftKindTone,
  reconciliationApi,
  type DriftFindingKind,
} from '../../api/reconciliation'
import { Button } from '../../components/ui/Button'

const kinds: { value: DriftFindingKind; label: string; hint: string }[] = [
  { value: 'Changed', label: 'Changed', hint: 'The record and the device both have a value and they disagree.' },
  { value: 'Missing', label: 'Missing', hint: 'The record says something the network no longer confirms.' },
  { value: 'New', label: 'New', hint: 'A scan observed something the record leaves blank.' },
]

const fields = [
  { value: 'location', label: 'Location' },
  { value: 'hostname', label: 'Hostname' },
  { value: 'managementIp', label: 'Management IP' },
  { value: 'lastSeen', label: 'Last seen' },
]

/**
 * Where the CMDB and the network disagree.
 *
 * The report exists because a scan never writes a CI's own attributes (WP-4.2) and never turns an
 * observed cable into a relationship (WP-4.3) — so what an operator asserted and what the estate
 * answered are still two separate records, and the difference between them is readable here.
 */
export function DriftReportPage() {
  const navigate = useNavigate()
  const [kind, setKind] = useState<DriftFindingKind | ''>('')
  const [field, setField] = useState('')
  const [siteId, setSiteId] = useState('')

  const sites = useQuery({ queryKey: ['directory-sites'], queryFn: () => directoryApi.listSites() })
  const drift = useQuery({
    queryKey: ['drift', kind, field, siteId],
    queryFn: () => reconciliationApi.getDrift({
      kind: kind || undefined,
      field: field || undefined,
      siteId: siteId || undefined,
      pageSize: 100,
    }),
  })

  const report = drift.data
  const summary = report?.summary

  return <div className="space-y-6">
    <div className="flex flex-wrap items-center gap-3">
      {/* The warning belongs beside the rows it is about, not in a topbar subtitle. */}
      <p className="text-sm text-slate-500">Nothing here is applied for you — every line is a decision about which of the two is wrong.</p>
      <div className="flex flex-wrap gap-2 sm:ml-auto">
        <Button variant="secondary" onClick={() => navigate('/audits')}><ScanSearch size={18} />Physical audits</Button>
        <Button variant="secondary" onClick={() => navigate('/assets/discovery')}><Radar size={18} />Review queue</Button>
      </div>
    </div>

    <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <Kpi label="CIs a scan has seen" value={summary?.cisObserved} tone="text-blue-600 bg-blue-50 dark:bg-blue-500/15" icon={<Radar size={20} />} />
      <Kpi label="Changed fields" value={summary?.changed} tone="text-red-600 bg-red-50 dark:bg-red-500/15" icon={<GitCompareArrows size={20} />} />
      <Kpi label="Missing" value={summary?.missing} tone="text-amber-600 bg-amber-50 dark:bg-amber-500/15" icon={<GitCompareArrows size={20} />} />
      <Kpi label="Unrecorded cables" value={summary?.unrecordedLinks} tone="text-slate-600 bg-slate-100 dark:bg-slate-500/15" icon={<Cable size={20} />} />
    </div>

    {summary !== undefined && summary.unmatchedDiscoveries > 0 && <div className="rounded-xl border border-slate-200 bg-white p-4 text-sm dark:border-slate-800 dark:bg-slate-900">
      <span className="text-slate-600 dark:text-slate-300">
        {summary.unmatchedDiscoveries} discover{summary.unmatchedDiscoveries === 1 ? 'y answers' : 'ies answer'} to no CI at all.
      </span>
      <Link to="/assets/discovery" className="ml-2 font-medium text-blue-600 hover:underline">Open the review queue<ArrowRight size={14} className="ml-1 inline" /></Link>
    </div>}

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <select aria-label="Filter by finding" className="input w-auto min-w-40" value={kind} onChange={(event) => setKind(event.target.value as DriftFindingKind | '')}>
          <option value="">Every finding</option>
          {kinds.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
        </select>
        <select aria-label="Filter by field" className="input w-auto min-w-40" value={field} onChange={(event) => setField(event.target.value)}>
          <option value="">Every field</option>
          {fields.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
        </select>
        <select aria-label="Filter by site" className="input w-auto min-w-44" value={siteId} onChange={(event) => setSiteId(event.target.value)}>
          <option value="">Every site</option>
          {(sites.data ?? []).map((site) => <option key={site.id} value={site.id}>{site.name}</option>)}
        </select>
        {kind && <p className="self-center text-[13px] text-slate-500">{kinds.find((option) => option.value === kind)?.hint}</p>}
      </div>

      {drift.isLoading ? <TableSkeleton />
        : drift.isError ? <ErrorState error={drift.error} retry={() => void drift.refetch()} />
        : (report?.items.length ?? 0) === 0 ? <EmptyState observed={summary?.cisObserved ?? 0} />
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[900px] text-left text-sm">
              <thead><tr>
                {['Configuration item', 'Field', 'Finding', 'Recorded', 'Observed'].map((header) =>
                  <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {report!.items.flatMap((item) => item.findings.map((finding, index) =>
                  <tr key={`${item.ciId}-${finding.field}`} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                    <td className="h-12 px-4">
                      {/* One row per finding, and the CI is named once: a switch with three
                          disagreements is one thing to go and look at, not three. */}
                      {index === 0 && <>
                        <Link to={`/assets/${item.ciId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{item.name}</Link>
                        <span className="ml-2 text-xs text-slate-500">{ciTypeLabel(item.type)}</span>
                      </>}
                    </td>
                    <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{finding.label}</td>
                    <td className="h-12 px-4">
                      <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${driftKindTone(finding.kind)}`}>{driftKindLabel(finding.kind)}</span>
                    </td>
                    <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{finding.recordedValue ?? <span className="text-slate-400">—</span>}</td>
                    <td className="h-12 px-4 text-slate-600 dark:text-slate-300">
                      {finding.field === 'lastSeen' && finding.observedValue
                        ? `Last seen ${new Date(finding.observedValue).toLocaleString()}`
                        : finding.observedValue ?? <span className="text-slate-400">Not reported</span>}
                    </td>
                  </tr>))}
              </tbody>
            </table>
          </div>}

      <footer className="border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800">
        {summary
          ? `${report!.items.length} of ${summary.cisWithDrift} CIs with drift, out of ${summary.cisObserved} a scan has reported · unseen for more than ${summary.staleAfterDays} days counts as missing`
          : ' '}
      </footer>
    </section>

    <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="font-semibold">Cables nobody recorded</h2>
      <p className="mt-1 text-sm text-slate-500">
        Links a device reported over LLDP or CDP that no relationship in the CMDB describes. A scan
        observes and an operator asserts, so nothing here is written down until somebody says so.
      </p>
      {drift.isPending && <div aria-label="Loading unrecorded links" className="mt-4 space-y-2">
        {[0, 1, 2].map((index) => <div key={index} className="h-5 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
      </div>}
      {report && (report.unrecordedLinks.length === 0
        ? <p className="mt-3 text-sm text-slate-500">Every observed link matches a relationship somebody recorded.</p>
        : <ul className="mt-4 divide-y divide-slate-200 text-sm dark:divide-slate-800">
            {report.unrecordedLinks.map((link) => <li key={`${link.sourceCiId}-${link.targetCiId}`} className="flex flex-wrap items-center gap-x-2 py-2">
              <Link to={`/assets/${link.sourceCiId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{link.sourceCiName}</Link>
              {link.sourcePort && <span className="text-xs text-slate-500">{link.sourcePort}</span>}
              <Cable size={15} className="text-slate-400" />
              <Link to={`/assets/${link.targetCiId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{link.targetCiName}</Link>
              {link.targetPort && <span className="text-xs text-slate-500">{link.targetPort}</span>}
              <span className="ml-auto text-xs text-slate-500">
                {link.protocols.join(', ')}{link.confirmedByBothEnds ? ' · both ends agree' : ' · reported by one end'}
              </span>
            </li>)}
          </ul>)}
    </section>
  </div>
}

function Kpi({ label, value, tone, icon }: { label: string; value: number | undefined; tone: string; icon: ReactNode }) {
  return <div className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <span className={`grid size-10 place-items-center rounded-full ${tone}`}>{icon}</span>
    <p className="mt-3 text-[13px] text-slate-500">{label}</p>
    {/* A failed read reads "Unavailable" rather than 0: a zero is a claim about the estate (WP-2.11). */}
    <p className="mt-1 text-3xl font-bold tabular-nums">{value ?? <span className="text-base font-medium text-slate-400">Unavailable</span>}</p>
  </div>
}

function EmptyState({ observed }: { observed: number }) {
  return <div className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><GitCompareArrows /></span>
    <h2 className="mt-3 font-semibold">
      {observed === 0 ? 'No scan has reported a known CI yet' : 'The CMDB and the network agree'}
    </h2>
    <p className="mt-1 text-sm text-slate-500">
      {observed === 0
        ? 'Drift is computed from what discovery observes about CIs it has matched. Until a scan matches one, there is nothing to compare.'
        : `Every one of the ${observed} CIs a scan has reported matches what is recorded for it.`}
    </p>
  </div></div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) {
  return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span>
    <h2 className="mt-3 font-semibold">The drift report could not be loaded</h2>
    <p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div></div>
}

function TableSkeleton() {
  return <div aria-label="Loading drift" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
}
