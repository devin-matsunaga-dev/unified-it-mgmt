import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight, FileText, Plus, Search, Building2, BellRing, Settings } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import {
  contractStatusLabel,
  contractStatusTone,
  contractTypes,
  contractsApi,
  describeDaysRemaining,
  type ContractExpiryStatus,
  type ContractFilter,
  type ContractInput,
  type ContractType,
} from '../../api/contracts'
import { directoryApi } from '../../api/directory'
import { useAuth } from '../../auth/AuthProvider'
import { Button } from '../../components/ui/Button'
import { ContractFormDialog } from './ContractFormDialog'

/** Every agreement the estate runs on, soonest to expire first. */
export function ContractListPage() {
  const navigate = useNavigate()
  const { roles } = useAuth()
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<ContractFilter>({ page: 1, pageSize: 25 })
  const [dialogOpen, setDialogOpen] = useState(false)

  useEffect(() => {
    const timer = window.setTimeout(() => setFilter((current) => ({ ...current, search, page: 1 })), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  const contracts = useQuery({ queryKey: ['contracts', filter], queryFn: () => contractsApi.listContracts(filter), placeholderData: keepPreviousData })
  const vendors = useQuery({ queryKey: ['vendors'], queryFn: () => contractsApi.listVendors() })
  const users = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers })
  const departments = useQuery({ queryKey: ['directory', 'departments'], queryFn: directoryApi.listDepartments })

  const create = useMutation({
    mutationFn: (input: ContractInput) => contractsApi.createContract(input),
    onSuccess: async (contract) => {
      await queryClient.invalidateQueries({ queryKey: ['contracts'] })
      toast.success(`${contract.name} created`)
      setDialogOpen(false)
    },
  })

  // The pass is idempotent, so triggering it by hand only ever raises what is genuinely due today.
  const runNotices = useMutation({
    mutationFn: () => contractsApi.runExpiryPass(),
    onSuccess: (run) => toast.success(run.raised.length === 0
      ? 'Nothing is due — no notices raised.'
      : `${run.raised.length} renewal notice${run.raised.length === 1 ? '' : 's'} raised.`),
    onError: (error: Error) => toast.error(error.message),
  })

  const page = filter.page ?? 1
  const pageSize = filter.pageSize ?? 25
  const total = contracts.data?.total ?? 0
  const lastPage = Math.max(Math.ceil(total / pageSize), 1)
  const goToPage = (next: number) => setFilter((current) => ({ ...current, page: Math.min(Math.max(next, 1), lastPage) }))

  return <div className="space-y-6">
    <div className="flex flex-wrap justify-end gap-2">
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" disabled={runNotices.isPending} onClick={() => runNotices.mutate()}>
          <BellRing size={18} />{runNotices.isPending ? 'Checking…' : 'Check renewals now'}
        </Button>
        <Button variant="secondary" onClick={() => navigate('/contracts/vendors')}><Building2 size={18} />Vendors</Button>
        {/* Admins only: the settings page itself is Admin-gated, so showing this to a technician would
            offer a button that lands on "forbidden". */}
        {roles.includes('Admin') && <Button
          variant="secondary"
          onClick={() => navigate('/admin/settings/renewal-reminders')}
        ><Settings size={18} />Reminder settings</Button>}
        <Button onClick={() => setDialogOpen(true)}><Plus size={18} />New contract</Button>
      </div>
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="flex h-10 min-w-60 flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
          <Search size={17} /><span className="sr-only">Search contracts</span>
          <input value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search numbers, names, and vendors…" />
        </label>
        <select aria-label="Filter by status" className="input w-auto min-w-44" value={filter.status ?? ''} onChange={(event) => setFilter((current) => ({ ...current, status: (event.target.value || undefined) as ContractExpiryStatus | undefined, page: 1 }))}>
          <option value="">All statuses</option>
          <option value="ExpiringSoon">Expiring soon</option>
          <option value="Expired">Expired</option>
          <option value="Active">Active</option>
        </select>
        <select aria-label="Filter by type" className="input w-auto min-w-40" value={filter.type ?? ''} onChange={(event) => setFilter((current) => ({ ...current, type: (event.target.value || undefined) as ContractType | undefined, page: 1 }))}>
          <option value="">All types</option>
          {contractTypes.map((type) => <option key={type} value={type}>{type}</option>)}
        </select>
        <select aria-label="Filter by vendor" className="input w-auto min-w-44" value={filter.vendorId ?? ''} onChange={(event) => setFilter((current) => ({ ...current, vendorId: event.target.value || undefined, page: 1 }))}>
          <option value="">All vendors</option>
          {(vendors.data?.items ?? []).map((vendor) => <option key={vendor.id} value={vendor.id}>{vendor.name}</option>)}
        </select>
      </div>

      {contracts.isLoading ? <TableSkeleton />
        : contracts.isError ? <ErrorState error={contracts.error} retry={() => void contracts.refetch()} />
        : (contracts.data?.items.length ?? 0) === 0 ? <EmptyState onCreate={() => setDialogOpen(true)} />
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[900px] text-left text-sm">
              <thead><tr>
                {['PO number', 'Name', 'Vendor', 'Type', 'Ends', 'Status', 'Covered CIs'].map((header) => <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {contracts.data!.items.map((contract) => <tr key={contract.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4 font-mono text-xs text-slate-500">{contract.poNumber}</td>
                  <td className="h-12 px-4"><Link to={`/contracts/${contract.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{contract.name}</Link></td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{contract.vendorName}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{contract.type}</td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{contract.endDate} <span className="text-xs text-slate-500">({describeDaysRemaining(contract.daysRemaining)})</span></td>
                  <td className="h-12 px-4"><span className={`rounded-md px-2 py-0.5 text-xs font-medium ${contractStatusTone(contract.status)}`}>{contractStatusLabel(contract.status)}</span></td>
                  <td className="h-12 px-4 text-right tabular-nums text-slate-600 dark:text-slate-300">{contract.coveredCiCount}</td>
                </tr>)}
              </tbody>
            </table>
          </div>}

      <footer className="flex items-center border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800">
        <span>{contracts.data?.items.length ?? 0} of {total} contracts</span>
        <div className="ml-auto flex items-center gap-1">
          <span className="mr-2">Page {page} of {lastPage}</span>
          <Button variant="ghost" className="size-8 p-0" disabled={page <= 1} onClick={() => goToPage(page - 1)} aria-label="Previous page"><ChevronLeft size={16} /></Button>
          <Button variant="ghost" className="size-8 p-0" disabled={page >= lastPage} onClick={() => goToPage(page + 1)} aria-label="Next page"><ChevronRight size={16} /></Button>
        </div>
      </footer>
    </section>

    <ContractFormDialog open={dialogOpen} contract={null} vendors={vendors.data?.items ?? []} users={users.data ?? []}
      departments={departments.data ?? []}
      pending={create.isPending} error={create.error instanceof Error ? create.error.message : undefined}
      onClose={() => { if (!create.isPending) { setDialogOpen(false); create.reset() } }}
      onSubmit={async (input) => { await create.mutateAsync(input) }} />
  </div>
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return <div className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><FileText /></span>
    <h2 className="mt-3 font-semibold">No matching contracts</h2>
    <p className="mt-1 text-sm text-slate-500">Adjust the filters, or record the first agreement — you will need its vendor first.</p>
    <Button className="mt-4" onClick={onCreate}>New contract</Button>
  </div></div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) {
  return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span>
    <h2 className="mt-3 font-semibold">Contracts could not be loaded</h2>
    <p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div></div>
}

function TableSkeleton() {
  return <div aria-label="Loading contracts" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
}
