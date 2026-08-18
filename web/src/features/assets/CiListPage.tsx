import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Layers, Pencil, Plus, QrCode, Search, Server, SlidersHorizontal, Upload } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { assetsApi, ciTypeLabel, ciTypes, type Ci, type CiFilter, type CiLifecycleState, type CiType } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import { Button } from '../../components/ui/Button'
import { CiBulkEditDialog } from './CiBulkEditDialog'
import { CiFormDialog, type CiFormSubmit } from './CiFormDialog'
import { CiLabelDialog } from './CiLabelDialog'
import { CiLifecycleDrawer } from './CiLifecycleDrawer'
import { CiStatsRow } from './CiStatsRow'
import { ciSortDescription, ciSortLabels, nextCiSort, sortCis, type CiSort, type CiSortColumn } from './ciSort'
import { ciLifecycleLabel, ciLifecycleStates, ciLifecycleTone } from './lifecycle'

export function CiListPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<CiFilter>({ page: 1, pageSize: 25 })
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editing, setEditing] = useState<Ci | null>(null)
  const [peeking, setPeeking] = useState<Ci | null>(null)
  const [selectedIds, setSelectedIds] = useState<string[]>([])
  const [bulkOpen, setBulkOpen] = useState(false)
  const [labelsOpen, setLabelsOpen] = useState(false)
  // Sorting is applied in the browser, like the ticket list. `/api/cis` orders by name and takes no
  // sort parameter, so this reorders the page on screen and nothing beyond it — see `sortNote` below.
  const [sort, setSort] = useState<CiSort | null>(null)

  // Search runs on the server, so keystrokes are debounced into the query filter.
  useEffect(() => {
    const timer = window.setTimeout(() => setFilter((current) => ({ ...current, search, page: 1 })), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  const cis = useQuery({ queryKey: ['cis', filter], queryFn: () => assetsApi.listCis(filter), placeholderData: keepPreviousData })
  // Always refetched: a cached schema can omit a field an admin has since made required, producing
  // a 400 the form cannot attribute to any input.
  const schemas = useQuery({ queryKey: ['ci-type-schemas'], queryFn: assetsApi.listTypeSchemas, staleTime: 0, refetchOnMount: 'always' })

  /**
   * The "choose one" fields of the selected type, which become the sub-filters beside it. Every
   * Select field gets one rather than a single privileged "category": nothing in the model makes one
   * field special, and a type with two of them is narrowed by both.
   */
  const subFilters = useMemo(
    () => filter.type === undefined
      ? []
      : (schemas.data?.find((schema) => schema.type === filter.type)?.customFields ?? [])
        .filter((field) => field.type === 'Select' && field.options.length > 0),
    [schemas.data, filter.type])
  const lifecycleStates = useQuery({ queryKey: ['ci-lifecycle-states'], queryFn: assetsApi.listLifecycleStates })
  const owners = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers })

  // The drawer holds a snapshot, so a transition made inside it has to be re-read from the refreshed
  // list or the buttons would still offer the old state's targets.
  const peeked = peeking ? cis.data?.items.find((item) => item.id === peeking.id) ?? peeking : null

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

  // The selection is kept as ids and resolved against the current page, so a filter change or a page
  // turn silently drops what is no longer on screen rather than editing rows nobody can see.
  const visible = sortCis(cis.data?.items ?? [], sort)
  const selection = visible.filter((ci) => selectedIds.includes(ci.id))
  const allVisibleSelected = visible.length > 0 && selection.length === visible.length
  const toggleAll = () => setSelectedIds(allVisibleSelected ? [] : visible.map((ci) => ci.id))
  const toggleOne = (id: string) => setSelectedIds((current) =>
    current.includes(id) ? current.filter((item) => item !== id) : [...current, id])

  const page = filter.page ?? 1
  const pageSize = filter.pageSize ?? 25
  const total = cis.data?.total ?? 0
  const lastPage = Math.max(Math.ceil(total / pageSize), 1)
  const goToPage = (next: number) => setFilter((current) => ({ ...current, page: Math.min(Math.max(next, 1), lastPage) }))

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div><h1 className="text-[28px] font-bold">Assets</h1><p className="mt-1 text-sm text-slate-500">The configuration items every ticket, alert, and device links back to.</p></div>
      <div className="flex gap-2 sm:ml-auto">
        <Button variant="secondary" onClick={() => navigate('/assets/import')}><Upload size={18} />Import</Button>
        <Button onClick={() => { setEditing(null); setDialogOpen(true) }}><Plus size={18} />New CI</Button>
      </div>
    </div>

    {/* A tile sets the whole narrowing it counts, so the number on it and the rows below always agree. */}
    <CiStatsRow filter={filter} onSelect={(next) => setFilter((current) => ({
      ...current, lifecycleState: undefined, warrantyExpiringWithinDays: undefined, ...next, page: 1,
    }))} />

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      {selection.length > 0 &&<div className="flex flex-wrap items-center gap-3 border-b border-slate-200 bg-blue-50 px-4 py-3 text-[13px] dark:border-slate-800 dark:bg-blue-500/10">
        <span className="font-medium text-blue-700 dark:text-blue-400">{selection.length} selected</span>
        <Button variant="secondary" className="h-8 px-3 text-[13px]" onClick={() => setBulkOpen(true)}><Layers size={15} />Bulk edit</Button>
        <Button variant="secondary" className="h-8 px-3 text-[13px]" onClick={() => setLabelsOpen(true)}><QrCode size={15} />Print labels</Button>
        <Button variant="ghost" className="h-8 px-3 text-[13px]" onClick={() => setSelectedIds([])}>Clear selection</Button>
      </div>}

      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="flex h-10 min-w-60 flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
          <Search size={17} /><span className="sr-only">Search configuration items</span>
          <input value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search names, asset tags, and serials…" />
        </label>
        <select aria-label="Filter by type" className="input w-auto min-w-40" value={filter.type ?? ''}
          onChange={(event) => setFilter((current) => ({
            ...current,
            type: (event.target.value || undefined) as CiType | undefined,
            // The sub-filters belong to the type being left, so they go with it. Keeping them would
            // silently narrow the new type by a field it does not have and return nothing.
            customFields: undefined,
            page: 1,
          }))}>
          <option value="">All types</option>
          {ciTypes.map((type) => <option key={type} value={type}>{ciTypeLabel(type)}</option>)}
        </select>

        {/*
          * The sub-filters: one per "choose one" field the selected type carries. They appear only
          * once a type is chosen, because a field belongs to a type and "All types" has none.
          */}
        {subFilters.map((field) => <select key={field.id}
          aria-label={`Filter by ${field.label}`}
          className="input w-auto min-w-40"
          value={filter.customFields?.find((item) => item.fieldId === field.id)?.value ?? ''}
          onChange={(event) => setFilter((current) => {
            const rest = (current.customFields ?? []).filter((item) => item.fieldId !== field.id)
            const chosen = event.target.value
            const next = chosen ? [...rest, { fieldId: field.id, value: chosen }] : rest
            return { ...current, customFields: next.length > 0 ? next : undefined, page: 1 }
          })}>
          <option value="">All {field.label.toLowerCase()}</option>
          {field.options.map((option) => <option key={option} value={option}>{option}</option>)}
        </select>)}
        <select aria-label="Filter by lifecycle state" className="input w-auto min-w-40" value={filter.lifecycleState ?? ''} onChange={(event) => setFilter((current) => ({ ...current, lifecycleState: (event.target.value || undefined) as CiLifecycleState | undefined, page: 1 }))}>
          <option value="">All lifecycle states</option>
          {ciLifecycleStates.map((state) => <option key={state} value={state}>{ciLifecycleLabel(state)}</option>)}
        </select>
        <select aria-label="Filter by owner" className="input w-auto min-w-44" value={filter.ownerUserId ?? ''} onChange={(event) => setFilter((current) => ({ ...current, ownerUserId: event.target.value || undefined, page: 1 }))}>
          <option value="">All owners</option>
          {(owners.data ?? []).map((user) => <option key={user.id} value={user.id}>{user.displayName}</option>)}
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
            <table className="w-full min-w-[1100px] text-left text-sm">
              <thead><tr>
                <th className="h-11 w-10 px-4">
                  <input type="checkbox" aria-label="Select every configuration item on this page" checked={allVisibleSelected} onChange={toggleAll} />
                </th>
                {(Object.keys(ciSortLabels) as CiSortColumn[]).map((column) => <th key={column} aria-sort={ciSortDescription(sort, column)} className="h-11 px-4 text-[13px] font-medium text-slate-500">
                  {/* Named for the action, not just the column: "Lifecycle" alone is also a row button. */}
                  <button type="button" aria-label={`Sort by ${ciSortLabels[column].toLowerCase()}`}
                    className="inline-flex items-center gap-1 rounded hover:text-slate-900 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:text-slate-100"
                    onClick={() => setSort((current) => nextCiSort(current, column))}>
                    {ciSortLabels[column]}
                    {sort?.column === column ? (sort.desc ? <ArrowDown size={14} /> : <ArrowUp size={14} />) : <ArrowUpDown size={14} className="text-slate-400" />}
                  </button>
                </th>)}
                <th className="h-11 px-4" />
              </tr></thead>
              <tbody>
                {visible.map((ci) => <tr key={ci.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4">
                    <input type="checkbox" aria-label={`Select ${ci.name}`} checked={selectedIds.includes(ci.id)} onChange={() => toggleOne(ci.id)} />
                  </td>
                  <td className="h-12 px-4"><Link to={`/assets/${ci.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{ci.name}</Link></td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{ciTypeLabel(ci.type)}</td>
                  <td className="h-12 px-4 font-mono text-xs text-slate-500">{ci.assetTag ?? '—'}</td>
                  <td className="h-12 px-4 font-mono text-xs text-slate-500">{ci.serialNumber ?? '—'}</td>
                  <td className="h-12 px-4"><span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(ci.lifecycleState)}`}>{ciLifecycleLabel(ci.lifecycleState)}</span></td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{ci.ownership.ownerName ?? '—'}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{ci.ownership.departmentName ?? '—'}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{ci.ownership.siteName ?? '—'}</td>
                  <td className="h-12 px-4"><StatePill isActive={ci.isActive} /></td>
                  <td className="h-12 px-4 text-right whitespace-nowrap">
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" onClick={() => { setEditing(ci); setDialogOpen(true) }}><Pencil size={15} />Edit</Button>
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" onClick={() => setPeeking(ci)}><SlidersHorizontal size={15} />Lifecycle</Button>
                  </td>
                </tr>)}
              </tbody>
            </table>
          </div>}

      <footer className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800">
        <span>{cis.data?.items.length ?? 0} of {total} configuration items</span>
        {/* Sorting happens after paging, so on a multi-page estate it reorders this page alone. Saying
            so is the difference between a limitation and a wrong answer nobody can see. */}
        {sort && lastPage > 1 && <span className="text-amber-700 dark:text-amber-400">
          Sorted within this page of {pageSize} — not across all {total}
        </span>}
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

    <CiLifecycleDrawer ci={peeked} states={lifecycleStates.data ?? []} onClose={() => setPeeking(null)} />

    <CiBulkEditDialog selection={bulkOpen ? selection : []} onClose={() => setBulkOpen(false)}
      onApplied={() => { setBulkOpen(false); setSelectedIds([]) }} />

    <CiLabelDialog selection={labelsOpen ? selection : []} onClose={() => setLabelsOpen(false)} />
  </div>
}

function StatePill({ isActive }: { isActive: boolean }) {
  return isActive
    ? <span className="rounded-md bg-green-100 px-2 py-0.5 text-xs font-medium text-green-700 dark:bg-green-500/15 dark:text-green-400">Active</span>
    : <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-500/15 dark:text-slate-400">Inactive</span>
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return <div className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15 dark:text-blue-400"><Server /></span>
    <h2 className="mt-3 font-semibold">No matching configuration items</h2>
    <p className="mt-1 text-sm text-slate-500">Adjust the search or filters, or register the first CI.</p>
    <Button className="mt-4" onClick={onCreate}>Create CI</Button>
  </div></div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) {
  return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600 dark:bg-red-500/15 dark:text-red-400">!</span>
    <h2 className="mt-3 font-semibold">Configuration items could not be loaded</h2>
    <p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div></div>
}

function CiTableSkeleton() {
  return <div aria-label="Loading configuration items" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
}
