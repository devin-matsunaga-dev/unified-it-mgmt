import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { contractStatusLabel, contractStatusTone } from '../../api/contracts'
import {
  complianceLabel,
  complianceTone,
  describeOverage,
  softwareApi,
  softwareMatchKinds,
  type SoftwareMatchKind,
} from '../../api/software'
import { Button } from '../../components/ui/Button'

/**
 * One product: which machines carry it, what entitles them, and the rules that decide what counts as
 * this product at all. The three questions an over-deployment raises, on one page.
 */
export function SoftwareProductPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const [pattern, setPattern] = useState('')
  const [matchKind, setMatchKind] = useState<SoftwareMatchKind>('Prefix')
  const [confirmingRule, setConfirmingRule] = useState<string | null>(null)

  const compliance = useQuery({ queryKey: ['software-compliance', '', ''], queryFn: () => softwareApi.getCompliance() })
  const installs = useQuery({
    queryKey: ['installed-software', { productId: id }],
    queryFn: () => softwareApi.listInstalls({ productId: id, pageSize: 200 }),
  })
  const rules = useQuery({ queryKey: ['software-rules', id], queryFn: () => softwareApi.listRules(id) })
  const pools = useQuery({ queryKey: ['license-pools', id], queryFn: () => softwareApi.listPools({ productId: id, pageSize: 200 }) })

  const addRule = useMutation({
    mutationFn: () => softwareApi.createRule({ productId: id, matchKind, pattern, priority: 0 }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['software-rules', id] })
      setPattern('')
      toast.success('Rule added. Re-normalise to apply it to installs already recorded.')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const deleteRule = useMutation({
    mutationFn: (ruleId: string) => softwareApi.deleteRule(ruleId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['software-rules', id] })
      setConfirmingRule(null)
      toast.success('Rule removed. Installs keep their product until the next re-normalisation.')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const row = compliance.data?.rows.find((candidate) => candidate.productId === id)

  return <div className="space-y-6">
    <div>
      <Link to="/software" className="inline-flex items-center gap-1 text-[13px] text-slate-500 hover:text-blue-600"><ArrowLeft size={15} />Back to software</Link>
      <h1 className="mt-2 text-[28px] font-bold">{row?.productName ?? 'Product'}</h1>
      <p className="mt-1 text-sm text-slate-500">
        {row ? `${row.publisher}${row.category ? ` · ${row.category}` : ''}` : 'Loading the compliance position for this product…'}
      </p>
    </div>

    {row && <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap items-center gap-x-8 gap-y-3">
        <Figure label="Installed on" value={`${row.installedCiCount} device${row.installedCiCount === 1 ? '' : 's'}`} />
        <Figure label="Entitled" value={String(row.entitled)} />
        <Figure label="Balance" value={describeOverage(row)} />
        <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${complianceTone(row.state)}`}>{complianceLabel(row.state)}</span>
      </div>
      {row.expiredPoolCount > 0 && <p className="mt-3 text-sm text-amber-700 dark:text-amber-400">
        {row.expiredPoolCount} pool{row.expiredPoolCount === 1 ? ' has' : 's have'} expired and no longer entitle anything.
      </p>}
    </section>}

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <h2 className="border-b border-slate-200 p-4 font-semibold dark:border-slate-800">Installed on</h2>
      {installs.isPending ? <Skeleton label="Loading installs" />
        : installs.isError ? <p role="alert" className="p-4 text-sm text-red-600">Installs could not be loaded.</p>
        : installs.data!.items.length === 0 ? <p className="p-4 text-sm text-slate-500">No machine reports this product.</p>
        : <div className="overflow-x-auto"><table className="w-full min-w-[700px] text-left text-sm">
            <thead><tr>{['Machine', 'Reported as', 'Version', 'Installed on', 'Last seen in'].map((header) =>
              <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}</tr></thead>
            <tbody>
              {installs.data!.items.map((install) => <tr key={install.id} className="border-t border-slate-200 dark:border-slate-800">
                <td className="h-12 px-4"><Link to={`/assets/${install.ciId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{install.ciName}</Link></td>
                <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{install.rawName}</td>
                <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{install.version ?? '—'}</td>
                <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{install.installedOn ?? '—'}</td>
                <td className="h-12 px-4 text-slate-500">{install.source}</td>
              </tr>)}
            </tbody>
          </table></div>}
    </section>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <h2 className="border-b border-slate-200 p-4 font-semibold dark:border-slate-800">Licence pools</h2>
      {pools.isPending ? <Skeleton label="Loading licence pools" />
        : pools.isError ? <p role="alert" className="p-4 text-sm text-red-600">Licence pools could not be loaded.</p>
        : pools.data!.items.length === 0 ? <p className="p-4 text-sm text-slate-500">Nothing entitles this product. Every install of it is unlicensed.</p>
        : <ul className="divide-y divide-slate-200 text-sm dark:divide-slate-800">
            {pools.data!.items.map((pool) => <li key={pool.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 p-4">
              <Link to="/software/licenses" className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{pool.name}</Link>
              <span className="tabular-nums text-slate-600 dark:text-slate-300">{pool.entitlements} seats</span>
              {pool.reference && <span className="font-mono text-xs text-slate-500">{pool.reference}</span>}
              <span className="ml-auto text-slate-500">
                {pool.expiresAt
                  ? <>ends {pool.expiresAt} {pool.status && <span className={`ml-1 rounded-md px-2 py-0.5 text-xs font-medium ${contractStatusTone(pool.status)}`}>{contractStatusLabel(pool.status)}</span>}</>
                  : 'Perpetual'}
              </span>
            </li>)}
          </ul>}
    </section>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <h2 className="border-b border-slate-200 p-4 font-semibold dark:border-slate-800">Catalogue rules</h2>
      <p className="px-4 pt-4 text-sm text-slate-500">
        A raw name is tested against every rule most specific kind first: an exact match beats a prefix,
        which beats a contains. Adding a rule here only affects future imports until you re-normalise.
      </p>
      {rules.isPending ? <Skeleton label="Loading rules" />
        : rules.isError ? <p role="alert" className="p-4 text-sm text-red-600">Rules could not be loaded.</p>
        : <ul className="mt-2 divide-y divide-slate-200 text-sm dark:divide-slate-800">
            {rules.data!.map((rule) => <li key={rule.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
              <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">{rule.matchKind}</span>
              <span className="font-mono text-slate-900 dark:text-slate-100">{rule.pattern}</span>
              {confirmingRule === rule.id
                ? <span className="ml-auto flex items-center gap-2">
                    <Button variant="secondary" className="h-8" onClick={() => setConfirmingRule(null)}>Cancel</Button>
                    <Button variant="secondary" className="h-8 border-red-200 text-red-600 hover:bg-red-50 dark:border-red-500/30 dark:text-red-400 dark:hover:bg-red-500/10"
                      disabled={deleteRule.isPending} onClick={() => deleteRule.mutate(rule.id)}>Confirm remove</Button>
                  </span>
                : <Button variant="ghost" className="ml-auto h-8" onClick={() => setConfirmingRule(rule.id)}><Trash2 size={16} />Remove</Button>}
            </li>)}
          </ul>}
      <form
        className="flex flex-wrap items-end gap-3 border-t border-slate-200 p-4 dark:border-slate-800"
        onSubmit={(event) => { event.preventDefault(); if (pattern.trim()) addRule.mutate() }}
      >
        <label className="block">
          <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Match</span>
          <select aria-label="Match kind" className="input h-10 w-36" value={matchKind} onChange={(event) => setMatchKind(event.target.value as SoftwareMatchKind)}>
            {softwareMatchKinds.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
          </select>
        </label>
        <label className="block min-w-60 flex-1">
          <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Pattern</span>
          <input className="input h-10" maxLength={300} value={pattern} placeholder="microsoft office professional plus" onChange={(event) => setPattern(event.target.value)} />
        </label>
        <Button type="submit" disabled={addRule.isPending || !pattern.trim()}><Plus size={18} />Add rule</Button>
      </form>
    </section>

    {compliance.isError && <p role="alert" className="text-sm text-red-600">
      {compliance.error instanceof ApiError ? compliance.error.message : 'The compliance position could not be loaded.'}
    </p>}
  </div>
}

function Figure({ label, value }: { label: string; value: string }) {
  return <span className="block">
    <span className="block text-[13px] text-slate-500">{label}</span>
    <span className="block text-xl font-semibold tabular-nums">{value}</span>
  </span>
}

function Skeleton({ label }: { label: string }) {
  return <div aria-label={label} className="space-y-2 p-4">{[0, 1, 2].map((index) => <div key={index} className="h-8 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
}
