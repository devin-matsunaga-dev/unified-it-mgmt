import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Columns3, ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Layers, Pencil, Plus, QrCode, ScanLine, Search, Server, SlidersHorizontal, Upload } from 'lucide-react'
import { Fragment, useEffect, useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { assetsApi, type Ci, type CiFilter } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { CiBulkEditDialog } from './CiBulkEditDialog'
import { CiFormDialog, type CiFormSeed, type CiFormSubmit } from './CiFormDialog'
import { ScanDeviceDialog } from './ScanDeviceDialog'
import { CiLabelDialog } from './CiLabelDialog'
import { CiLifecycleDrawer } from './CiLifecycleDrawer'
import { CiStatsRow } from './CiStatsRow'
import { toTileFilter } from './ciTiles'
import { ciSortDescription, nextCiSort, sortCis, type CiSort, type CiSortColumn } from './ciSort'
import { ciColumn, ciColumnIds } from './ciColumns'
import { isColumnVisible, moveColumn, readLayout, toggleColumn, visibleColumns, writeLayout } from '../../lib/tableLayout'
import { ciFilterDefinition, ciFilterIds, clearFilter } from './ciFilters'

/** Namespaced so a later table on another screen cannot collide with this one's arrangement. */
const columnLayoutKey = 'assets:columns'
const filterLayoutKey = 'assets:filters'

export function CiListPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<CiFilter>({ page: 1, pageSize: 25 })
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editing, setEditing] = useState<Ci | null>(null)
  const [scanOpen, setScanOpen] = useState(false)
  /** What a confirmed identification carries into the New CI form. Null for an ordinary create. */
  const [seed, setSeed] = useState<CiFormSeed | null>(null)
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
  /**
   * Per browser, like the ticket list's column menu — a table arrangement is a personal preference,
   * not something to make everyone who shares an account live with.
   */
  const [layout, setLayout] = useState(() => readLayout(columnLayoutKey, ciColumnIds))
  const [draggingColumn, setDraggingColumn] = useState<CiSortColumn | null>(null)
  const [columnMenu, setColumnMenu] = useState(false)
  const [filterLayout, setFilterLayout] = useState(() => readLayout(filterLayoutKey, ciFilterIds))
  const [filterMenu, setFilterMenu] = useState(false)

  useEffect(() => writeLayout(filterLayoutKey, filterLayout), [filterLayout])

  const shownFilters = useMemo(
    () => visibleColumns(filterLayout).map(ciFilterDefinition),
    [filterLayout])

  useEffect(() => writeLayout(columnLayoutKey, layout), [layout])

  const shownColumns = useMemo(() => visibleColumns(layout).map(ciColumn), [layout])

  const subFilters = useMemo(
    () => filter.type === undefined
      ? []
      : (schemas.data?.find((schema) => schema.type === filter.type)?.customFields ?? [])
        .filter((field) => field.type === 'Select' && field.options.length > 0),
    [schemas.data, filter.type])
  const lifecycleStates = useQuery({ queryKey: ['ci-lifecycle-states'], queryFn: assetsApi.listLifecycleStates })
  const owners = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers })

  /** Sorted by name, because the directory's own order is not one anybody is scanning by. */
  const ownerOptions = useMemo(
    () => (owners.data ?? [])
      .map((user) => ({ value: user.id, label: user.displayName }))
      .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' })),
    [owners.data])


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
    <div className="flex flex-wrap justify-end gap-2">
      <Button variant="secondary" onClick={() => navigate('/assets/import')}><Upload size={18} />Import</Button>
      <Button variant="secondary" onClick={() => setScanOpen(true)}><ScanLine size={18} />Scan device</Button>
      <Button onClick={() => { setEditing(null); setSeed(null); setDialogOpen(true) }}><Plus size={18} />New CI</Button>
    </div>

    {/*
      * A tile sets the whole narrowing it counts, so the number on it and the rows below always
      * agree. Every constraint a tile can carry is dropped first, rather than the two the built-ins
      * happened to use — a pinned tile can narrow by type, owner or a custom field, and leaving any
      * of those behind means the next tile counts one thing while the table shows another.
      *
      * The search term goes too, and its box with it: a tile's count is taken without it, so a
      * surviving search would show fewer rows than the number promises.
      */}
    <CiStatsRow filter={filter} onSelect={(next) => {
      setSearch('')
      setFilter((current) => ({ pageSize: current.pageSize, ...toTileFilter(next), page: 1 }))
    }} />

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
        {shownFilters.map((definition) => <Fragment key={definition.id}>
          {definition.render({ filter, setFilter, ownerOptions, subFilters })}
        </Fragment>)}

        <div className="relative">
          <Button variant="secondary" onClick={() => setFilterMenu((value) => !value)} aria-expanded={filterMenu}>
            <SlidersHorizontal size={17} />Filters
          </Button>
          {filterMenu && <div className="absolute right-0 top-12 z-20 w-52 rounded-lg border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-700 dark:bg-slate-800">
            {ciFilterIds.map((id) => <label key={id} className="flex h-9 items-center gap-2 rounded px-2 text-sm hover:bg-slate-50 dark:hover:bg-slate-700">
              <input type="checkbox" checked={isColumnVisible(filterLayout, id)}
                onChange={() => setFilterLayout((current) => {
                  // Hiding a control clears what it was narrowing by, so nothing filters invisibly.
                  if (isColumnVisible(current, id)) setFilter((value) => clearFilter(id, value))
                  return toggleColumn(current, id)
                })} />
              {ciFilterDefinition(id).label}
            </label>)}
            <p className="mt-1 border-t border-slate-200 px-2 pt-2 text-[12px] text-slate-500 dark:border-slate-700">
              Search is always shown.
            </p>
          </div>}
        </div>

        <div className="relative">
          <Button variant="secondary" onClick={() => setColumnMenu((value) => !value)} aria-expanded={columnMenu}>
            <Columns3 size={17} />Columns
          </Button>
          {columnMenu && <div className="absolute right-0 top-12 z-20 w-52 rounded-lg border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-700 dark:bg-slate-800">
            {layout.order.map((id) => {
              const column = ciColumn(id)
              const shown = shownColumns.some((item) => item.id === id)
              return <label key={id} className="flex h-9 items-center gap-2 rounded px-2 text-sm hover:bg-slate-50 dark:hover:bg-slate-700">
                <input type="checkbox" checked={shown}
                  // The last one standing cannot be unticked; toggleColumn refuses it either way.
                  disabled={shown && shownColumns.length <= 1}
                  onChange={() => setLayout((current) => toggleColumn(current, id))} />
                {column.label}
              </label>
            })}
            <p className="mt-1 border-t border-slate-200 px-2 pt-2 text-[12px] text-slate-500 dark:border-slate-700">
              Drag a column heading to reorder it.
            </p>
          </div>}
        </div>
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
                {shownColumns.map((column) => <th key={column.id}
                  aria-sort={ciSortDescription(sort, column.id)}
                  // Native drag, as the dashboard's arrange mode uses. The sort control inside is a
                  // <button>, which has no drag behaviour of its own, so the two do not fight.
                  draggable
                  onDragStart={(event) => {
                    setDraggingColumn(column.id)
                    // Guarded: a real browser always provides it, but nothing here depends on it and
                    // assuming it exists turns a missing dataTransfer into a thrown handler.
                    if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
                  }}
                  onDragOver={(event) => { if (draggingColumn && draggingColumn !== column.id) event.preventDefault() }}
                  onDrop={(event) => {
                    event.preventDefault()
                    if (draggingColumn) setLayout((current) => moveColumn(current, draggingColumn, column.id))
                    setDraggingColumn(null)
                  }}
                  onDragEnd={() => setDraggingColumn(null)}
                  className={cn('h-11 cursor-grab px-4 text-[13px] font-medium text-slate-500',
                    draggingColumn === column.id && 'opacity-40')}>
                  {/* Named for the action, not just the column: "Lifecycle" alone is also a row button. */}
                  <button type="button" aria-label={`Sort by ${column.label.toLowerCase()}`}
                    className="inline-flex items-center gap-1 rounded hover:text-slate-900 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:text-slate-100"
                    onClick={() => setSort((current) => nextCiSort(current, column.id))}>
                    {column.label}
                    {sort?.column === column.id ? (sort.desc ? <ArrowDown size={14} /> : <ArrowUp size={14} />) : <ArrowUpDown size={14} className="text-slate-400" />}
                  </button>
                </th>)}
                <th className="h-11 px-4" />
              </tr></thead>
              <tbody>
                {visible.map((ci) => <tr key={ci.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4">
                    <input type="checkbox" aria-label={`Select ${ci.name}`} checked={selectedIds.includes(ci.id)} onChange={() => toggleOne(ci.id)} />
                  </td>
                  {shownColumns.map((column) => <td key={column.id} className={cn('h-12 px-4', column.className)}>
                    {column.cell(ci)}
                  </td>)}
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

    <ScanDeviceDialog
      open={scanOpen}
      onClose={() => setScanOpen(false)}
      onConfirm={(identified) => {
        // Confirming an identification opens the form; it does not create anything. The technician
        // still has to save, and everything here is editable before they do.
        setScanOpen(false)
        setEditing(null)
        setSeed(identified)
        setDialogOpen(true)
      }} />

    <CiFormDialog open={dialogOpen} ci={editing} seed={seed} schemas={schemas.data ?? []} pending={save.isPending}
      error={save.error instanceof Error ? save.error.message : undefined}
      onClose={() => { if (!save.isPending) closeDialog() }}
      onSubmit={async (input) => { await save.mutateAsync(input) }} />

    <CiLifecycleDrawer ci={peeked} states={lifecycleStates.data ?? []} onClose={() => setPeeking(null)} />

    <CiBulkEditDialog selection={bulkOpen ? selection : []} onClose={() => setBulkOpen(false)}
      onApplied={() => { setBulkOpen(false); setSelectedIds([]) }} />

    <CiLabelDialog selection={labelsOpen ? selection : []} onClose={() => setLabelsOpen(false)} />
  </div>
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
