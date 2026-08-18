import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AppWindow, BellRing, KeyRound, RefreshCw, Search, Upload } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { contractStatusLabel, contractStatusTone } from '../../api/contracts'
import {
  complianceLabel,
  complianceTone,
  describeOverage,
  softwareApi,
  type SoftwareComplianceState,
} from '../../api/software'
import { Button } from '../../components/ui/Button'

const states: { value: SoftwareComplianceState; label: string }[] = [
  { value: 'OverDeployed', label: 'Over-deployed' },
  { value: 'Unlicensed', label: 'Unlicensed' },
  { value: 'Compliant', label: 'Compliant' },
  { value: 'Unused', label: 'Unused' },
]

/**
 * Installed versus entitled, per product. This is the compliance report rather than a catalogue
 * listing: a product with no installs and no licence has nothing to say and is not on it.
 */
export function SoftwarePage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [params] = useSearchParams()
  const [search, setSearch] = useState('')
  const [debounced, setDebounced] = useState('')
  // Seeded from the URL once, so a WP-5.5 dashboard band opens this report on the state it counted.
  const [state, setState] = useState<SoftwareComplianceState | ''>(
    () => states.find((option) => option.value === params.get('compliance'))?.value ?? '')

  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(search), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  const compliance = useQuery({
    queryKey: ['software-compliance', state, debounced],
    queryFn: () => softwareApi.getCompliance(state || undefined, debounced || undefined),
  })
  const unrecognised = useQuery({
    queryKey: ['software-unrecognised'],
    queryFn: () => softwareApi.listUnrecognised(10),
  })

  // Re-running the catalogue is what makes a newly added rule reach the installs already recorded.
  const normalise = useMutation({
    mutationFn: () => softwareApi.normalise(),
    onSuccess: async (run) => {
      await queryClient.invalidateQueries({ queryKey: ['software-compliance'] })
      await queryClient.invalidateQueries({ queryKey: ['software-unrecognised'] })
      toast.success(run.normalised + run.renormalised === 0
        ? `Nothing changed — ${run.unrecognised} install${run.unrecognised === 1 ? '' : 's'} still match no rule.`
        : `${run.normalised} newly matched, ${run.renormalised} re-matched, ${run.unrecognised} still unrecognised.`)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  // Idempotent within a day, so pressing it only ever raises a shortfall that is genuinely there.
  const runCompliance = useMutation({
    mutationFn: () => softwareApi.runCompliance(),
    onSuccess: (run) => toast.success(run.raised.length === 0
      ? run.overDeployed === 0
        ? 'Nothing is over-deployed — no notices raised.'
        : 'Every over-deployment has already been notified today.'
      : `${run.raised.length} compliance notice${run.raised.length === 1 ? '' : 's'} raised.`),
    onError: (error: Error) => toast.error(error.message),
  })

  const report = compliance.data

  return <div className="space-y-6">
    <div className="flex flex-wrap justify-end gap-2">
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" disabled={runCompliance.isPending} onClick={() => runCompliance.mutate()}>
          <BellRing size={18} />{runCompliance.isPending ? 'Checking…' : 'Check compliance now'}
        </Button>
        <Button variant="secondary" disabled={normalise.isPending} onClick={() => normalise.mutate()}>
          <RefreshCw size={18} />{normalise.isPending ? 'Re-normalising…' : 'Re-normalise'}
        </Button>
        <Button variant="secondary" onClick={() => navigate('/software/licenses')}><KeyRound size={18} />Licences</Button>
        <Button onClick={() => navigate('/software/import')}><Upload size={18} />Import inventory</Button>
      </div>
    </div>

    <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <Kpi label="Products" value={report?.productCount} tone="text-blue-600 bg-blue-50 dark:bg-blue-500/15" icon={<AppWindow size={20} />} />
      <Kpi label="Installs recorded" value={report?.totalInstalls} tone="text-slate-600 bg-slate-100 dark:bg-slate-500/15" icon={<AppWindow size={20} />} />
      <Kpi label="Over-deployed" value={report?.overDeployedCount} tone="text-red-600 bg-red-50 dark:bg-red-500/15" icon={<BellRing size={20} />} />
      <Kpi label="Unlicensed" value={report?.unlicensedCount} tone="text-amber-600 bg-amber-50 dark:bg-amber-500/15" icon={<KeyRound size={20} />} />
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="flex h-10 min-w-60 flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
          <Search size={17} /><span className="sr-only">Search products</span>
          <input value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search products and publishers…" />
        </label>
        <select aria-label="Filter by compliance" className="input w-auto min-w-44" value={state} onChange={(event) => setState(event.target.value as SoftwareComplianceState | '')}>
          <option value="">All products</option>
          {states.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
        </select>
      </div>

      {compliance.isLoading ? <TableSkeleton />
        : compliance.isError ? <ErrorState error={compliance.error} retry={() => void compliance.refetch()} />
        : (report?.rows.length ?? 0) === 0 ? <EmptyState hasProducts={(report?.productCount ?? 0) > 0} onImport={() => navigate('/software/import')} />
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[900px] text-left text-sm">
              <thead><tr>
                {['Product', 'Publisher', 'Installed on', 'Entitled', 'Balance', 'Compliance', 'Licence ends'].map((header) =>
                  <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {report!.rows.map((row) => <tr key={row.productId} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4">
                    <Link to={`/software/products/${row.productId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{row.productName}</Link>
                    {row.category && <span className="ml-2 text-xs text-slate-500">{row.category}</span>}
                  </td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{row.publisher}</td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">
                    {row.installedCiCount} device{row.installedCiCount === 1 ? '' : 's'}
                  </td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{row.entitled}</td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{describeOverage(row)}</td>
                  <td className="h-12 px-4">
                    <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${complianceTone(row.state)}`}>{complianceLabel(row.state)}</span>
                  </td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">
                    {row.nextExpiry
                      ? <>{row.nextExpiry} {row.expiryStatus && <span className={`ml-1 rounded-md px-2 py-0.5 text-xs font-medium ${contractStatusTone(row.expiryStatus)}`}>{contractStatusLabel(row.expiryStatus)}</span>}</>
                      : <span className="text-slate-500">{row.licensePoolCount > 0 ? 'Perpetual' : '—'}</span>}
                  </td>
                </tr>)}
              </tbody>
            </table>
          </div>}

      <footer className="border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800">
        {report ? `${report.rows.length} of ${report.productCount} products · ${report.totalInstalls} installs against ${report.totalEntitled} entitlements` : ' '}
      </footer>
    </section>

    <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="font-semibold">Not recognised by the catalogue</h2>
      <p className="mt-1 text-sm text-slate-500">
        Raw names no normalisation rule claims. They are recorded against their machines and counted
        nowhere else — add a rule, then re-normalise, and the installs already imported follow it.
      </p>
      {unrecognised.isPending && <div aria-label="Loading unrecognised software" className="mt-4 space-y-2">
        {[0, 1, 2].map((index) => <div key={index} className="h-5 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
      </div>}
      {unrecognised.isError && <p role="alert" className="mt-3 text-sm text-red-600">The unrecognised list could not be loaded.</p>}
      {unrecognised.data && (unrecognised.data.length === 0
        ? <p className="mt-3 text-sm text-slate-500">Every installed name resolves to a catalogue product.</p>
        : <ul className="mt-4 divide-y divide-slate-200 text-sm dark:divide-slate-800">
            {unrecognised.data.map((row) => <li key={row.rawName} className="flex flex-wrap items-center gap-x-3 py-2">
              <span className="font-medium text-slate-900 dark:text-slate-100">{row.rawName}</span>
              {row.rawPublisher && <span className="text-slate-500">{row.rawPublisher}</span>}
              <span className="ml-auto tabular-nums text-slate-500">on {row.ciCount} device{row.ciCount === 1 ? '' : 's'}</span>
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

function EmptyState({ hasProducts, onImport }: { hasProducts: boolean; onImport: () => void }) {
  return <div className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><AppWindow /></span>
    <h2 className="mt-3 font-semibold">{hasProducts ? 'No products match these filters' : 'Nothing has been inventoried yet'}</h2>
    <p className="mt-1 text-sm text-slate-500">
      {hasProducts
        ? 'Adjust the filters to see the rest of the catalogue.'
        : 'Import an inventory file and its raw names are normalised into catalogue products as they land.'}
    </p>
    <Button className="mt-4" onClick={onImport}>Import inventory</Button>
  </div></div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) {
  return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span>
    <h2 className="mt-3 font-semibold">Compliance could not be loaded</h2>
    <p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div></div>
}

function TableSkeleton() {
  return <div aria-label="Loading compliance" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
}
