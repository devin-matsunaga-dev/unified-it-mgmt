import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, KeyRound, Pencil, Plus, Search, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { contractStatusLabel, contractStatusTone, describeDaysRemaining, type ContractExpiryStatus } from '../../api/contracts'
import { softwareApi, type LicensePool, type LicensePoolFilter, type LicensePoolInput } from '../../api/software'
import { Button } from '../../components/ui/Button'
import { LicensePoolDialog } from './LicensePoolDialog'

/** Every block of entitlements the estate holds, and when each one lapses. */
export function LicensePoolPage() {
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<LicensePoolFilter>({ page: 1, pageSize: 25 })
  const [editing, setEditing] = useState<LicensePool | null>(null)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null)

  useEffect(() => {
    const timer = window.setTimeout(() => setFilter((current) => ({ ...current, search, page: 1 })), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  const pools = useQuery({
    queryKey: ['license-pools', filter],
    queryFn: () => softwareApi.listPools(filter),
    placeholderData: keepPreviousData,
  })
  const products = useQuery({ queryKey: ['software-products'], queryFn: () => softwareApi.listProducts() })

  const save = useMutation({
    mutationFn: (input: LicensePoolInput) => editing
      ? softwareApi.updatePool(editing.id, { ...input, isActive: editing.isActive })
      : softwareApi.createPool(input),
    onSuccess: async (pool) => {
      await queryClient.invalidateQueries({ queryKey: ['license-pools'] })
      await queryClient.invalidateQueries({ queryKey: ['software-compliance'] })
      toast.success(`${pool.name} saved`)
      setDialogOpen(false)
      setEditing(null)
    },
  })

  const remove = useMutation({
    mutationFn: (id: string) => softwareApi.deletePool(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['license-pools'] })
      await queryClient.invalidateQueries({ queryKey: ['software-compliance'] })
      setConfirmingDelete(null)
      toast.success('Licence pool deleted. Its product is no longer entitled by it.')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  return <div className="space-y-6">
    <div className="flex flex-wrap items-center gap-3">
      <Link to="/software" className="inline-flex items-center gap-1 text-[13px] text-slate-500 hover:text-blue-600"><ArrowLeft size={15} />Back to software</Link>
      <p className="text-sm text-slate-500">An expired pool entitles nothing from the day it lapses.</p>
      <Button className="ml-auto" onClick={() => { setEditing(null); setDialogOpen(true) }}><Plus size={18} />New licence pool</Button>
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="flex h-10 min-w-60 flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
          <Search size={17} /><span className="sr-only">Search licence pools</span>
          <input value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search pools, references and products…" />
        </label>
        <select aria-label="Filter by status" className="input w-auto min-w-44" value={filter.status ?? ''}
          onChange={(event) => setFilter((current) => ({ ...current, status: (event.target.value || undefined) as ContractExpiryStatus | undefined, page: 1 }))}>
          <option value="">All dated pools and perpetual</option>
          <option value="ExpiringSoon">Expiring soon</option>
          <option value="Expired">Expired</option>
          <option value="Active">Active</option>
        </select>
      </div>

      {pools.isLoading ? <TableSkeleton />
        : pools.isError ? <ErrorState error={pools.error} retry={() => void pools.refetch()} />
        : (pools.data?.items.length ?? 0) === 0 ? <EmptyState onCreate={() => { setEditing(null); setDialogOpen(true) }} />
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[900px] text-left text-sm">
              <thead><tr>
                {['Pool', 'Product', 'Reference', 'Seats', 'Expires', 'Status', ''].map((header, index) =>
                  <th key={index} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {pools.data!.items.map((pool) => <tr key={pool.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4 font-medium text-slate-900 dark:text-slate-100">{pool.name}</td>
                  <td className="h-12 px-4">
                    <Link to={`/software/products/${pool.productId}`} className="text-slate-600 hover:text-blue-600 dark:text-slate-300">{pool.publisher} {pool.productName}</Link>
                  </td>
                  <td className="h-12 px-4 font-mono text-xs text-slate-500">{pool.reference ?? '—'}</td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{pool.entitlements}</td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">
                    {pool.expiresAt
                      ? <>{pool.expiresAt} {pool.daysRemaining !== null && <span className="text-xs text-slate-500">({describeDaysRemaining(pool.daysRemaining)})</span>}</>
                      : <span className="text-slate-500">Perpetual</span>}
                  </td>
                  <td className="h-12 px-4">
                    {pool.status
                      ? <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${contractStatusTone(pool.status)}`}>{contractStatusLabel(pool.status)}</span>
                      : <span className="text-xs text-slate-500">No end date</span>}
                  </td>
                  <td className="h-12 px-4 text-right">
                    {confirmingDelete === pool.id
                      ? <span className="flex items-center justify-end gap-2">
                          <Button variant="ghost" className="h-8" onClick={() => setConfirmingDelete(null)}>Cancel</Button>
                          <Button variant="secondary" className="h-8 border-red-200 text-red-600 hover:bg-red-50 dark:border-red-500/30 dark:text-red-400 dark:hover:bg-red-500/10"
                            disabled={remove.isPending} onClick={() => remove.mutate(pool.id)}>Confirm delete</Button>
                        </span>
                      : <span className="flex items-center justify-end gap-1">
                          <Button variant="ghost" className="size-8 p-0" aria-label={`Edit ${pool.name}`} onClick={() => { setEditing(pool); setDialogOpen(true) }}><Pencil size={16} /></Button>
                          <Button variant="ghost" className="size-8 p-0" aria-label={`Delete ${pool.name}`} onClick={() => setConfirmingDelete(pool.id)}><Trash2 size={16} /></Button>
                        </span>}
                  </td>
                </tr>)}
              </tbody>
            </table>
          </div>}

      <footer className="border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800">
        {pools.data?.items.length ?? 0} of {pools.data?.total ?? 0} licence pools
      </footer>
    </section>

    <LicensePoolDialog open={dialogOpen} pool={editing} products={products.data?.items ?? []}
      pending={save.isPending} error={save.error instanceof Error ? save.error.message : undefined}
      onClose={() => { if (!save.isPending) { setDialogOpen(false); setEditing(null); save.reset() } }}
      onSubmit={async (input) => { await save.mutateAsync(input) }} />
  </div>
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return <div className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><KeyRound /></span>
    <h2 className="mt-3 font-semibold">No matching licence pools</h2>
    <p className="mt-1 text-sm text-slate-500">Until a product is entitled by a pool, every install of it counts as unlicensed.</p>
    <Button className="mt-4" onClick={onCreate}>New licence pool</Button>
  </div></div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) {
  return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span>
    <h2 className="mt-3 font-semibold">Licence pools could not be loaded</h2>
    <p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div></div>
}

function TableSkeleton() {
  return <div aria-label="Loading licence pools" className="space-y-px p-4">{Array.from({ length: 5 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
}
