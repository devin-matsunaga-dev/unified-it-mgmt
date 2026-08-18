import { flexRender, getCoreRowModel, getSortedRowModel, useReactTable, type ColumnDef, type SortingState, type VisibilityState } from '@tanstack/react-table'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { SlidersHorizontal, ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Columns3, Plus, Search } from 'lucide-react'
import { Fragment, useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { helpdeskApi, type CreateTicketInput, type SaveTicketViewInput, type Ticket, type TicketFilter, type TicketPriority, type TicketView } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { useAuth } from '../../auth/AuthProvider'
import { directoryApi } from '../../api/directory'
import { flattenCategories } from './categoryFields'
import {
  clearTicketFilter, ticketFilterDefinition, ticketFilterIds,
} from './ticketFilterControls'
import { isColumnVisible, moveColumn, readLayout, toggleColumn, visibleColumns, writeLayout } from '../../lib/tableLayout'
import { PriorityPill, StatusPill, displayStatus, formatLocal, ticketPriorities, ticketStatuses } from './ticketUi'
import { QuickCreateTicket } from './QuickCreateTicket'
import { SavedViews } from './SavedViews'
import { emptyFilter, normalizeFilter } from './ticketFilters'

const savedFilterLayoutKey = 'tickets:filters'
const savedOrderKey = 'tickets:column-order'
const savedVisibilityKey = 'tickets.column-visibility'

/**
 * The filter a deep link asks for. `?priority=` and `?status=` may both repeat, which is how one link
 * arrives from a dashboard band and another from a saved bookmark; anything unrecognised is ignored rather
 * than emptying the list, because a link that narrowed to nothing looks exactly like a broken screen.
 */
function filterFromQuery(params: URLSearchParams): TicketFilter {
  const priorities = params.getAll('priority')
    .filter((value): value is TicketPriority => ticketPriorities.includes(value as TicketPriority))
  const statuses = params.getAll('status')
    .filter((value) => (ticketStatuses as readonly string[]).includes(value))
  const filter: TicketFilter = {}
  if (priorities.length) filter.priorities = priorities
  if (statuses.length) filter.statuses = statuses
  return filter
}
const columns: ColumnDef<Ticket>[] = [
  { accessorKey: 'number', header: 'ID', cell: ({ row }) => <Link className="font-mono text-xs text-blue-600 hover:underline" to={`/tickets/${row.original.id}`}>#{row.original.number}</Link> },
  { accessorKey: 'title', header: 'Title', cell: ({ row }) => <Link className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100" to={`/tickets/${row.original.id}`}>{row.original.title}</Link> },
  { accessorKey: 'status', header: 'Status', cell: ({ getValue }) => <StatusPill status={getValue<string>()} /> },
  { accessorKey: 'priority', header: 'Priority', cell: ({ getValue }) => <PriorityPill priority={getValue<Ticket['priority']>()} /> },
  { accessorKey: 'categoryName', header: 'Category', cell: ({ getValue }) => getValue<string | null>() ?? 'Uncategorised' },
  { accessorKey: 'requesterDepartmentName', header: 'Department', cell: ({ getValue }) => getValue<string | null>() ?? '—' },
  { accessorKey: 'requesterSiteName', header: 'Location', cell: ({ getValue }) => getValue<string | null>() ?? '—' },
  { accessorKey: 'queueName', header: 'Queue', cell: ({ getValue }) => getValue<string | null>() ?? 'Unqueued' },
  { accessorKey: 'assignedTechnicianId', header: 'Assignee', cell: ({ getValue }) => getValue<string | null>() ?? 'Unassigned' },
  { accessorKey: 'updatedAt', header: 'Updated', cell: ({ getValue }) => <span className="whitespace-nowrap text-slate-500">{formatLocal(getValue<string>())}</span> },
]

/** Every column's id, in the order they are defined — the arrangement nobody has changed. */
const columnIds = columns.map((column, index) =>
  ('accessorKey' in column ? String(column.accessorKey) : column.id) ?? String(index))


export function TicketListPage() {
  const { account } = useAuth()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [params] = useSearchParams()
  const [createOpen, setCreateOpen] = useState(false)
  const [sorting, setSorting] = useState<SortingState>([{ id: 'updatedAt', desc: true }])
  const [search, setSearch] = useState('')
  // Seeded from the URL once, so a WP-5.5 dashboard widget can open this list already narrowed. Read on
  // the first render rather than kept in sync: the filter controls below are the reader's, and a URL that
  // kept re-applying itself would undo the first thing they changed.
  const [filter, setFilter] = useState<TicketFilter>(() => filterFromQuery(params))
  const [activeViewId, setActiveViewId] = useState<string | null>(null)
  const [columnMenu, setColumnMenu] = useState(false)
  const [visibility, setVisibility] = useState<VisibilityState>(() => {
    try { return JSON.parse(localStorage.getItem(savedVisibilityKey) ?? '{}') as VisibilityState } catch { return {} }
  })
  // Full-text search runs on the server, so the keystrokes are debounced into the query filter.
  useEffect(() => {
    const timer = window.setTimeout(() => setFilter((current) => ({ ...current, search })), 200)
    return () => window.clearTimeout(timer)
  }, [search])
  // Previous rows stay on screen while the next search lands, so typing narrows the table instead of flashing a skeleton.
  const tickets = useQuery({ queryKey: ['tickets', normalizeFilter(filter)], queryFn: () => helpdeskApi.listTickets(filter), placeholderData: keepPreviousData })
  const queues = useQuery({ queryKey: ['queues'], queryFn: helpdeskApi.listQueues })
  const categories = useQuery({ queryKey: ['ticket-categories'], queryFn: helpdeskApi.listCategories, staleTime: 0, refetchOnMount: 'always' })
  const views = useQuery({ queryKey: ['ticket-views'], queryFn: helpdeskApi.listViews })
  const people = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers })
  const activeView = views.data?.find((view) => view.id === activeViewId) ?? null
  const create = useMutation({
    mutationFn: (input: CreateTicketInput) => helpdeskApi.createTicket(input),
    onSuccess: async (ticket) => { await queryClient.invalidateQueries({ queryKey: ['tickets'] }); setCreateOpen(false); toast.success(`${ticket.number} created`); navigate(`/tickets/${ticket.id}`) },
  })
  const refreshViews = () => queryClient.invalidateQueries({ queryKey: ['ticket-views'] })
  const saveView = useMutation({
    mutationFn: (input: SaveTicketViewInput) => helpdeskApi.createView(input),
    onSuccess: async (view) => { await refreshViews(); setActiveViewId(view.id); toast.success(`View "${view.name}" saved`) },
  })
  const updateView = useMutation({
    mutationFn: ({ id, input }: { id: string; input: SaveTicketViewInput }) => helpdeskApi.updateView(id, input),
    onSuccess: async (view) => { await refreshViews(); toast.success(`View "${view.name}" updated`) },
  })
  const deleteView = useMutation({
    mutationFn: (view: TicketView) => helpdeskApi.deleteView(view.id),
    onSuccess: async () => { await refreshViews(); setActiveViewId(null); setFilter(emptyFilter); setSearch(''); toast.success('View deleted') },
  })
  const applyView = (view: TicketView | null) => {
    setActiveViewId(view?.id ?? null)
    setFilter(view?.filter ?? emptyFilter)
    setSearch(view?.filter.search ?? '')
  }
  const patchFilter = (patch: Partial<TicketFilter>) => setFilter((current) => ({ ...current, ...patch }))
  /**
   * Reconciled against the columns that exist now rather than trusted as stored: a column added in a
   * later release would otherwise be invisible to anybody who had ever dragged one, and a removed one
   * would linger in the order forever.
   */
  const [columnOrder, setColumnOrder] = useState<string[]>(
    () => readLayout(savedOrderKey, columnIds).order)
  const [draggingColumn, setDraggingColumn] = useState<string | null>(null)

  useEffect(() => writeLayout(savedOrderKey, { order: columnOrder, hidden: [] }), [columnOrder])

  const table = useReactTable({
    data: tickets.data?.items ?? [], columns,
    state: { sorting, columnVisibility: visibility, columnOrder },
    onSortingChange: setSorting,
    onColumnOrderChange: setColumnOrder,
    onColumnVisibilityChange: (updater) => setVisibility((current) => { const next = typeof updater === 'function' ? updater(current) : updater; localStorage.setItem(savedVisibilityKey, JSON.stringify(next)); return next }),
    getCoreRowModel: getCoreRowModel(), getSortedRowModel: getSortedRowModel(),
  })
  const categoryOptions = useMemo(() => flattenCategories(categories.data ?? []), [categories.data])

  /**
   * Who a ticket can be assigned to. End users raise tickets but never take them, so offering the
   * whole directory would bury the handful of people who actually appear in the Assignee column.
   */
  const assignees = useMemo(
    () => (people.data ?? [])
      .filter((user) => user.role !== 'EndUser')
      .sort((a, b) => a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' })),
    [people.data])

  /** Per browser, like the column menu beside it — a filter bar is a personal arrangement. */
  const [filterLayout, setFilterLayout] = useState(() => readLayout(savedFilterLayoutKey, ticketFilterIds))
  const [filterMenu, setFilterMenu] = useState(false)

  useEffect(() => writeLayout(savedFilterLayoutKey, filterLayout), [filterLayout])

  const shownFilters = useMemo(
    () => visibleColumns(filterLayout).map(ticketFilterDefinition),
    [filterLayout])
  const viewPending = saveView.isPending || updateView.isPending || deleteView.isPending

  return <div className="space-y-6">
    <div className="flex flex-wrap justify-end gap-2"><Button onClick={() => setCreateOpen(true)}><Plus size={18} />New ticket</Button></div>
    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <SavedViews views={views.data ?? []} activeView={activeView} filter={filter} pending={viewPending}
        username={account?.username ?? ''}
        onApply={applyView}
        onApplyFilter={(next) => { setActiveViewId(null); setFilter(next); setSearch(next.search ?? '') }}
        onSave={({ name, isShared }) => saveView.mutate({ name, isShared, filter: normalizeFilter(filter) })}
        onUpdate={(view) => updateView.mutate({ id: view.id, input: { name: view.name, isShared: view.isShared, filter: normalizeFilter(filter) } })}
        onDelete={(view) => deleteView.mutate(view)} />
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="flex h-10 min-w-60 flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700"><Search size={17} /><span className="sr-only">Search tickets</span><input value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search titles, descriptions, and comments…" /></label>
        {shownFilters.map((definition) => <Fragment key={definition.id}>
          {definition.render({ filter, patchFilter, queues: queues.data ?? [], categoryOptions, assignees })}
        </Fragment>)}

        <div className="relative">
          <Button variant="secondary" onClick={() => setFilterMenu((value) => !value)} aria-expanded={filterMenu}>
            <SlidersHorizontal size={17} />Filters
          </Button>
          {filterMenu && <div className="absolute right-0 top-12 z-10 w-56 rounded-lg border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-700 dark:bg-slate-800">
            {ticketFilterIds.map((id) => <label key={id} className="flex h-9 items-center gap-2 rounded px-2 text-sm hover:bg-slate-50 dark:hover:bg-slate-700">
              <input type="checkbox" checked={isColumnVisible(filterLayout, id)}
                onChange={() => setFilterLayout((current) => {
                  // Hiding a control clears what it was narrowing by, so nothing filters invisibly.
                  if (isColumnVisible(current, id)) setFilter((value) => clearTicketFilter(id, value))
                  return toggleColumn(current, id)
                })} />
              {ticketFilterDefinition(id).label}
            </label>)}
            <p className="mt-1 border-t border-slate-200 px-2 pt-2 text-[12px] text-slate-500 dark:border-slate-700">
              Search is always shown.
            </p>
          </div>}
        </div>
        <div className="relative"><Button variant="secondary" onClick={() => setColumnMenu((value) => !value)} aria-expanded={columnMenu}><Columns3 size={17} />Columns</Button>{columnMenu && <div className="absolute right-0 top-12 z-10 w-48 rounded-lg border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-700 dark:bg-slate-800">{table.getAllLeafColumns().map((column) => <label key={column.id} className="flex h-9 items-center gap-2 rounded px-2 text-sm hover:bg-slate-50 dark:hover:bg-slate-700"><input type="checkbox" checked={column.getIsVisible()} onChange={column.getToggleVisibilityHandler()} />{typeof column.columnDef.header === 'string' ? column.columnDef.header : column.id}</label>)}</div>}</div>
      </div>
      {tickets.isLoading ? <TicketTableSkeleton /> : tickets.isError ? <ErrorState error={tickets.error} retry={() => void tickets.refetch()} /> : table.getRowModel().rows.length === 0 ? <div className="grid min-h-64 place-items-center p-8 text-center"><div><span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600"><Search /></span><h2 className="mt-3 font-semibold">No matching tickets</h2><p className="mt-1 text-sm text-slate-500">Adjust the search or filters, or create a new ticket.</p><Button className="mt-4" onClick={() => setCreateOpen(true)}>Create ticket</Button></div></div> : <div className="overflow-x-auto"><table className="w-full min-w-[900px] text-left text-sm"><thead><tr>{table.getHeaderGroups()[0].headers.map((header) => <th key={header.id}
                  /* Native drag, as the asset table's headings use. The sort control inside is a
                     <button>, which has no drag behaviour of its own, so the two do not fight. */
                  draggable
                  onDragStart={(event) => { setDraggingColumn(header.column.id); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move' }}
                  onDragOver={(event) => { if (draggingColumn && draggingColumn !== header.column.id) event.preventDefault() }}
                  onDrop={(event) => { event.preventDefault(); if (draggingColumn) setColumnOrder(moveColumn({ order: columnOrder, hidden: [] }, draggingColumn, header.column.id).order); setDraggingColumn(null) }}
                  onDragEnd={() => setDraggingColumn(null)}
                  className={`h-11 cursor-grab px-4 text-[13px] font-medium text-slate-500${draggingColumn === header.column.id ? ' opacity-40' : ''}`}><button className="inline-flex items-center gap-1" onClick={header.column.getToggleSortingHandler()}>{flexRender(header.column.columnDef.header, header.getContext())}{header.column.getCanSort() && (header.column.getIsSorted() === 'asc' ? <ArrowUp size={14} /> : header.column.getIsSorted() === 'desc' ? <ArrowDown size={14} /> : <ArrowUpDown size={14} />)}</button></th>)}</tr></thead><tbody>{table.getRowModel().rows.map((row) => <tr key={row.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">{row.getVisibleCells().map((cell) => <td key={cell.id} className="h-12 px-4 text-slate-600 dark:text-slate-300">{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>)}</tr>)}</tbody></table></div>}
      <footer className="flex items-center border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800"><span>{table.getRowModel().rows.length} of {tickets.data?.total ?? 0} tickets</span><div className="ml-auto flex gap-1"><Button variant="ghost" className="size-8 p-0" disabled aria-label="Previous page"><ChevronLeft size={16} /></Button><Button variant="ghost" className="size-8 p-0" disabled aria-label="Next page"><ChevronRight size={16} /></Button></div></footer>
    </section>
    <QuickCreateTicket open={createOpen} pending={create.isPending} error={create.error instanceof Error ? create.error.message : undefined} queues={queues.data ?? []} categories={categories.data ?? []} onClose={() => { if (!create.isPending) { setCreateOpen(false); create.reset() } }} onSubmit={async (input) => { await create.mutateAsync(input) }} />
  </div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) { return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div><span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span><h2 className="mt-3 font-semibold">Tickets could not be loaded</h2><p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p><Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button></div></div> }
function TicketTableSkeleton() { return <div aria-label="Loading tickets" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div> }
