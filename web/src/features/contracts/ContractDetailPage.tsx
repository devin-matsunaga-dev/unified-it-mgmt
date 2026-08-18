import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Boxes, Pencil, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi, ciTypeLabel } from '../../api/assets'
import {
  contractStatusLabel,
  contractStatusTone,
  contractsApi,
  describeDaysRemaining,
  type ContractInput,
} from '../../api/contracts'
import { directoryApi } from '../../api/directory'
import { Button } from '../../components/ui/Button'
import { ciLifecycleLabel, ciLifecycleTone } from '../assets/lifecycle'
import { ContractFormDialog } from './ContractFormDialog'
import { usePageHeading } from '../../layout/pageHeading'

/** One agreement: its terms, and every CI it covers. */
export function ContractDetailPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)

  const contract = useQuery({ queryKey: ['contracts', id], queryFn: () => contractsApi.getContract(id), enabled: Boolean(id) })
  usePageHeading(contract.data ? { title: contract.data.name } : null)
  const [covered, vendors, users] = useQueries({ queries: [
    { queryKey: ['cis', { contractId: id }], queryFn: () => assetsApi.listCis({ contractId: id, pageSize: 200 }), enabled: Boolean(id) },
    { queryKey: ['vendors'], queryFn: () => contractsApi.listVendors() },
    { queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers },
  ] })

  const save = useMutation({
    mutationFn: (input: ContractInput) => contractsApi.updateContract(id, { ...input, isActive: contract.data?.isActive ?? true }),
    onSuccess: async (updated) => {
      await queryClient.invalidateQueries({ queryKey: ['contracts'] })
      setEditing(false)
      toast.success(`${updated.name} updated`)
    },
  })

  const remove = useMutation({
    mutationFn: () => contractsApi.deleteContract(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['contracts'] })
      toast.success('Contract deleted')
      navigate('/contracts')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (contract.isLoading) return <DetailSkeleton />
  if (contract.isError || !contract.data) return <div role="alert" className="rounded-xl border border-red-200 bg-white p-8 text-center dark:border-red-900 dark:bg-slate-900">
    <h1 className="font-semibold">Contract could not be loaded</h1>
    <p className="mt-2 text-sm text-slate-500">{contract.error instanceof Error ? contract.error.message : 'The contract may not exist.'}</p>
    <Button className="mt-4" variant="secondary" onClick={() => void contract.refetch()}>Try again</Button>
  </div>

  const item = contract.data
  const cis = covered.data?.items ?? []

  return <div className="space-y-6">
    <Link to="/contracts" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to contracts</Link>

    <header className="flex flex-col gap-4 xl:flex-row xl:items-start">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-mono text-sm text-slate-500">{item.contractNumber}</span>
          <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${contractStatusTone(item.status)}`}>{contractStatusLabel(item.status)}</span>
          {item.autoRenews && <span className="rounded-md bg-blue-100 px-2 py-0.5 text-xs font-medium text-blue-700 dark:bg-blue-500/15 dark:text-blue-400">Auto-renews</span>}
          {!item.isActive && <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-500/15 dark:text-slate-400">Inactive</span>}
        </div>
        <h1 className="mt-2 text-[28px] font-bold leading-tight">{item.name}</h1>
        <p className="mt-2 text-sm text-slate-500">{item.type} agreement with {item.vendorName} · ends {item.endDate} ({describeDaysRemaining(item.daysRemaining)})</p>
      </div>
      <div className="flex gap-2 xl:ml-auto">
        <Button variant="secondary" onClick={() => setEditing(true)}><Pencil size={16} />Edit</Button>
        <Button variant="secondary" disabled={remove.isPending}
          onClick={() => { if (window.confirm(`Delete ${item.name}? This cannot be undone.`)) remove.mutate() }}>
          <Trash2 size={16} />Delete
        </Button>
      </div>
    </header>

    <div className="grid gap-6 xl:grid-cols-[minmax(0,2fr)_minmax(300px,1fr)]">
      <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
          <div>
            <h2 className="font-semibold">Covered assets</h2>
            <p className="mt-1 text-sm text-slate-500">Every CI this contract covers.</p>
          </div>
          <span className="ml-auto text-[13px] text-slate-500">{covered.data?.total ?? 0} assets</span>
        </div>
        {covered.isLoading ? <div aria-label="Loading covered assets" className="space-y-2 p-5">{Array.from({ length: 3 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}</div>
          : covered.isError ? <div role="alert" className="p-5 text-sm text-red-600">Covered assets could not be loaded.</div>
          : cis.length === 0 ? <div className="grid place-items-center p-8 text-center"><div>
              <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><Boxes /></span>
              <p className="mt-3 text-sm text-slate-500">Nothing is covered yet. Attach this contract from an asset's "Warranty &amp; contract" card.</p>
            </div></div>
          : <ul className="divide-y divide-slate-200 dark:divide-slate-800">
              {cis.map((ci) => <li key={ci.id} className="flex flex-wrap items-center gap-3 p-4">
                <Link to={`/assets/${ci.id}`} className="min-w-0 flex-1 truncate font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{ci.name}</Link>
                <span className="text-xs text-slate-500">{ciTypeLabel(ci.type)}</span>
                {ci.assetTag && <span className="font-mono text-xs text-slate-500">{ci.assetTag}</span>}
                <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(ci.lifecycleState)}`}>{ciLifecycleLabel(ci.lifecycleState)}</span>
                <span className="text-xs text-slate-500">{ci.coverage.warrantyExpiresAt ? `Warranty ends ${ci.coverage.warrantyExpiresAt}` : 'No warranty date'}</span>
              </li>)}
            </ul>}
      </section>

      <aside className="space-y-6">
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Terms</h2>
          <dl className="mt-4 space-y-3 text-sm">
            <Detail label="Vendor" value={item.vendorName} />
            <Detail label="Type" value={item.type} />
            <Detail label="Starts" value={item.startDate} />
            <Detail label="Ends" value={item.endDate} />
            <Detail label="Renewal" value={item.autoRenews ? 'Automatic' : 'Manual'} />
            <Detail label="Cost" value={item.cost === null ? '—' : `${item.cost.toLocaleString()} ${item.currency ?? ''}`.trim()} />
            <Detail label="Owner" value={item.ownerName ?? 'Unassigned'} />
            <Detail label="Notices to" value={item.ownerEmail ?? 'The asset mailbox'} />
          </dl>
          {item.notes && <p className="mt-4 whitespace-pre-wrap border-t border-slate-200 pt-4 text-sm leading-6 text-slate-600 dark:border-slate-800 dark:text-slate-300">{item.notes}</p>}
        </section>
      </aside>
    </div>

    <ContractFormDialog open={editing} contract={item} vendors={vendors.data?.items ?? []} users={users.data ?? []}
      pending={save.isPending} error={save.error instanceof Error ? save.error.message : undefined}
      onClose={() => { if (!save.isPending) { setEditing(false); save.reset() } }}
      onSubmit={async (input) => { await save.mutateAsync(input) }} />
  </div>
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div className="flex gap-4"><dt className="text-slate-500">{label}</dt><dd className="ml-auto max-w-[65%] break-words text-right font-medium">{value}</dd></div>
}

function DetailSkeleton() {
  return <div aria-label="Loading contract" className="space-y-6">
    <div className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
    <div className="grid gap-6 xl:grid-cols-[2fr_1fr]">
      <div className="h-96 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
      <div className="h-72 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
    </div>
  </div>
}
