import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight, Plus, Search, Server } from 'lucide-react'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { assetsApi, ciTypeLabel, ciTypes, type Ci, type CiFilter, type CiType } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { CiFormDialog, type CiFormSubmit } from './CiFormDialog'

export function CiListPage() {
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<CiFilter>({ page: 1, pageSize: 25 })
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editing, setEditing] = useState<Ci | null>(null)

  // Search runs on the server, so keystrokes are debounced into the query filter.
  useEffect(() => {
    const timer = window.setTimeout(() => setFilter((current) => ({ ...current, search, page: 1 })), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  const cis = useQuery({ queryKey: ['cis', filter], queryFn: () => assetsApi.listCis(filter), placeholderData: keepPreviousData })
  // Always refetched: a cached schema can omit a field an admin has since made required, producing
  // a 400 the form cannot attribute to any input.
  const schemas = useQuery({ queryKey: ['ci-type-schemas'], queryFn: assetsApi.listTypeSchemas, staleTime: 0, refetchOnMount: 'always' })

  const closeDialog = () => { setDialogOpen(false); setEditing(null); save.reset() }
  const save = useMutation({
    mutationFn: (input: CiFormSubmit) => editing
      ? assetsApi.updateCi(editing.id, { name: input.name, assetTag: input.assetTag, serialNumber: input.serialNumber, description: input.description, isActive: input.isActive, attributes: input.attributes, customFields: input.customFields })
      : assetsApi.createCi({ type: input.type, name: input.name, assetTag: input.assetTag, serialNumber: input.serialNumber, description: input.description, attributes: input.attributes, customFields: input.customFields }),
    onSuccess: async (ci) => {
      await queryClient.invalidateQueries({ queryKey: ['cis'] })
      toast.success(`${ci.name} ${editing ? 'updated' : 'created'}`)
      setDialogOpen(false)
      setEditing(null)
    },
  })

  const page = filter.page ?? 1
  const pageSize = filter.pageSize ?? 25
  const total = cis.data?.total ?? 0
  const lastPage = Math.max(Math.ceil(total / pageSize), 1)
  const goToPage = (next: number) => setFilter((current) => ({ ...current, page: Math.min(Math.max(next, 1), lastPage) }))

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div><h1 className="text-[28px] font-bold">Assets</h1><p className="mt-1 text-sm text-slate-500">The configuration items every ticket, alert, and device links back to.</p></div>
      <Button className="sm:ml-auto" onClick={() => { setEditing(null); setDialogOpen(true) }}><Plus size={18} />New CI</Button>
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="flex h-10 min-w-60 flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
          <Search size={17} /><span className="sr-only">Search configuration items</span>
          <input value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search names, asset tags, and serials…" />
        </label>
        <select aria-label="Filter by type" className="input w-auto min-w-40" value={filter.type ?? ''} onChange={(event) => setFilter((current) => ({ ...current, type: (event.target.value || undefined) as CiType | undefined, page: 1 }))}>
          <option value="">All types</option>
          {ciTypes.map((type) => <option key={type} value={type}>{ciTypeLabel(type)}</option>)}
        </select>
        <select aria-label="Filter by state" className="input w-auto min-w-36" value={filter.isActive === undefined ? '' : String(filter.isActive)} onChange={(event) => setFilter((current) => ({ ...current, isActive: event.target.value === '' ? undefined : event.target.value === 'true', page: 1 }))}>
          <option value="">Active and inactive</option>
          <option value="true">Active only</option>
          <option value="false">Inactive only</option>
        </select>
      </div>

      {cis.isLoading ? <CiTableSkeleton />
        : cis.isError ? <ErrorState error={cis.error} retry={() => void cis.refetch()} />
        : (cis.data?.items.length ?? 0) === 0 ? <EmptyState onCreate={() => { setEditing(null); setDialogOpen(true) }} />
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[900px] text-left text-sm">
              <thead><tr>
                {['Name', 'Type', 'Asset tag', 'Serial', 'Attributes', 'State'].map((header) => <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {cis.data!.items.map((ci) => <tr key={ci.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4"><button className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100" onClick={() => { setEditing(ci); setDialogOpen(true) }}>{ci.name}</button></td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{ciTypeLabel(ci.type)}</td>
                  <td className="h-12 px-4 font-mono text-xs text-slate-500">{ci.assetTag ?? '—'}</td>
                  <td className="h-12 px-4 font-mono text-xs text-slate-500">{ci.serialNumber ?? '—'}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{summariseAttributes(ci)}</td>
                  <td className="h-12 px-4"><StatePill isActive={ci.isActive} /></td>
                </tr>)}
              </tbody>
            </table>
          </div>}

      <footer className="flex items-center border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800">
        <span>{cis.data?.items.length ?? 0} of {total} configuration items</span>
        <div className="ml-auto flex items-center gap-1">
          <span className="mr-2">Page {page} of {lastPage}</span>
          <Button variant="ghost" className="size-8 p-0" disabled={page <= 1} onClick={() => goToPage(page - 1)} aria-label="Previous page"><ChevronLeft size={16} /></Button>
          <Button variant="ghost" className="size-8 p-0" disabled={page >= lastPage} onClick={() => goToPage(page + 1)} aria-label="Next page"><ChevronRight size={16} /></Button>
        </div>
      </footer>
    </section>

    <CiFormDialog open={dialogOpen} ci={editing} schemas={schemas.data ?? []} pending={save.isPending}
      error={save.error instanceof Error ? save.error.message : undefined}
      onClose={() => { if (!save.isPending) closeDialog() }}
      onSubmit={async (input) => { await save.mutateAsync(input) }} />
  </div>
}

function summariseAttributes(ci: Ci) {
  const summary = Object.entries(ci.attributes).filter(([, value]) => value !== '').slice(0, 2).map(([, value]) => value).join(' · ')
  return summary || '—'
}

function StatePill({ isActive }: { isActive: boolean }) {
  return isActive
    ? <span className="rounded-md bg-green-100 px-2 py-0.5 text-xs font-medium text-green-700 dark:bg-green-500/15 dark:text-green-400">Active</span>
    : <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-500/15 dark:text-slate-400">Inactive</span>
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return <div className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600"><Server /></span>
    <h2 className="mt-3 font-semibold">No matching configuration items</h2>
    <p className="mt-1 text-sm text-slate-500">Adjust the search or filters, or register the first CI.</p>
    <Button className="mt-4" onClick={onCreate}>Create CI</Button>
  </div></div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) {
  return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span>
    <h2 className="mt-3 font-semibold">Configuration items could not be loaded</h2>
    <p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div></div>
}

function CiTableSkeleton() {
  return <div aria-label="Loading configuration items" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
}
