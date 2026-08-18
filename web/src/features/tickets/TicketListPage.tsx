import { flexRender, getCoreRowModel, getSortedRowModel, useReactTable, type ColumnDef, type SortingState, type VisibilityState } from '@tanstack/react-table'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Columns3, Plus, Search } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { helpdeskApi, type CreateTicketInput, type SaveTicketViewInput, type Ticket, type TicketFilter, type TicketPriority, type TicketType, type TicketView } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { flattenCategories } from './categoryFields'
import { PriorityPill, StatusPill, displayStatus, formatLocal, ticketPriorities, ticketStatuses } from './ticketUi'
import { QuickCreateTicket } from './QuickCreateTicket'
import { SavedViews } from './SavedViews'
import { emptyFilter, normalizeFilter } from './ticketFilters'

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
/**
 * The quick switch between the two kinds. Incidents read INC- and service requests REQ-, so this is
 * the control that stops an alert-raised incident being answered as though somebody asked for it.
 * `undefined` is "all" — the filter drops empty members, so it has to be absent rather than a value.
 */
const ticketKindTabs: { label: string; value: TicketType | undefined }[] = [
  { label: 'All', value: undefined },
  { label: 'Incidents', value: 'Incident' },
  { label: 'Service requests', value: 'ServiceRequest' },
]

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

export function TicketListPage() {
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
  const table = useReactTable({
    data: tickets.data?.items ?? [], columns, state: { sorting, columnVisibility: visibility },
    onSortingChange: setSorting,
    onColumnVisibilityChange: (updater) => setVisibility((current) => { const next = typeof updater === 'function' ? updater(current) : updater; localStorage.setItem(savedVisibilityKey, JSON.stringify(next)); return next }),
    getCoreRowModel: getCoreRowModel(), getSortedRowModel: getSortedRowModel(),
  })
  const categoryOptions = useMemo(() => flattenCategories(categories.data ?? []), [categories.data])
  const viewPending = saveView.isPending || updateView.isPending || deleteView.isPending

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end"><div><h1 className="text-[28px] font-bold">Tickets</h1><p className="mt-1 text-sm text-slate-500">Triage, assign, and resolve service work.</p></div><Button className="sm:ml-auto" onClick={() => setCreateOpen(true)}><Plus size={18} />New ticket</Button></div>
    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <SavedViews views={views.data ?? []} activeView={activeView} filter={filter} pending={viewPending}
        onApply={applyView}
        onSave={({ name, isShared }) => saveView.mutate({ name, isShared, filter: normalizeFilter(filter) })}
        onUpdate={(view) => updateView.mutate({ id: view.id, input: { name: view.name, isShared: view.isShared, filter: normalizeFilter(filter) } })}
        onDelete={(view) => deleteView.mutate(view)} />
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <div className="flex h-10 items-center rounded-lg border border-slate-200 p-0.5 dark:border-slate-700" role="group" aria-label="Filter by kind">
          {ticketKindTabs.map(({ label, value }) => <button key={label} type="button"
            aria-pressed={(filter.type ?? undefined) === value}
            onClick={() => patchFilter({ type: value })}
            className={`h-9 rounded-md px-3 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 ${(filter.type ?? undefined) === value ? 'bg-blue-600 text-white' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800'}`}>
            {label}
          </button>)}
        </div>
        <label className="flex h-10 min-w-60 flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700"><Search size={17} /><span className="sr-only">Search tickets</span><input value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search titles, descriptions, and comments…" /></label>
        <select aria-label="Filter by status" className="input w-auto min-w-36" value={filter.statuses?.[0] ?? ''} onChange={(event) => patchFilter({ statuses: event.target.value ? [event.target.value] : undefined })}><option value="">All statuses</option>{ticketStatuses.map((status) => <option key={status} value={status}>{displayStatus(status)}</option>)}</select>
        <select aria-label="Filter by priority" className="input w-auto min-w-36" value={filter.priorities?.[0] ?? ''} onChange={(event) => patchFilter({ priorities: event.target.value ? [event.target.value as TicketPriority] : undefined })}><option value="">All priorities</option>{ticketPriorities.map((priority) => <option key={priority}>{priority}</option>)}</select>
        <select aria-label="Filter by queue" className="input w-auto min-w-36" value={filter.queueId ?? ''} onChange={(event) => patchFilter({ queueId: event.target.value || undefined })}><option value="">All queues</option>{queues.data?.map((queue) => <option key={queue.id} value={queue.id}>{queue.name}</option>)}</select>
        <select aria-label="Filter by category" className="input w-auto min-w-36" value={filter.categoryId ?? ''} onChange={(event) => patchFilter({ categoryId: event.target.value || undefined })}><option value="">All categories</option>{categoryOptions.map((option) => <option key={option.id} value={option.id}>{' '.repeat(option.depth * 2)}{option.name}</option>)}</select>
        <label className="flex h-10 items-center gap-2 text-sm text-slate-600 dark:text-slate-300"><input type="checkbox" checked={filter.unassigned ?? false} onChange={(event) => patchFilter({ unassigned: event.target.checked || undefined })} />Unassigned only</label>
        <div className="relative"><Button variant="secondary" onClick={() => setColumnMenu((value) => !value)} aria-expanded={columnMenu}><Columns3 size={17} />Columns</Button>{columnMenu && <div className="absolute right-0 top-12 z-10 w-48 rounded-lg border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-700 dark:bg-slate-800">{table.getAllLeafColumns().map((column) => <label key={column.id} className="flex h-9 items-center gap-2 rounded px-2 text-sm hover:bg-slate-50 dark:hover:bg-slate-700"><input type="checkbox" checked={column.getIsVisible()} onChange={column.getToggleVisibilityHandler()} />{typeof column.columnDef.header === 'string' ? column.columnDef.header : column.id}</label>)}</div>}</div>
      </div>
      {tickets.isLoading ? <TicketTableSkeleton /> : tickets.isError ? <ErrorState error={tickets.error} retry={() => void tickets.refetch()} /> : table.getRowModel().rows.length === 0 ? <div className="grid min-h-64 place-items-center p-8 text-center"><div><span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600"><Search /></span><h2 className="mt-3 font-semibold">No matching tickets</h2><p className="mt-1 text-sm text-slate-500">Adjust the search or filters, or create a new ticket.</p><Button className="mt-4" onClick={() => setCreateOpen(true)}>Create ticket</Button></div></div> : <div className="overflow-x-auto"><table className="w-full min-w-[900px] text-left text-sm"><thead><tr>{table.getHeaderGroups()[0].headers.map((header) => <th key={header.id} className="h-11 px-4 text-[13px] font-medium text-slate-500"><button className="inline-flex items-center gap-1" onClick={header.column.getToggleSortingHandler()}>{flexRender(header.column.columnDef.header, header.getContext())}{header.column.getCanSort() && (header.column.getIsSorted() === 'asc' ? <ArrowUp size={14} /> : header.column.getIsSorted() === 'desc' ? <ArrowDown size={14} /> : <ArrowUpDown size={14} />)}</button></th>)}</tr></thead><tbody>{table.getRowModel().rows.map((row) => <tr key={row.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">{row.getVisibleCells().map((cell) => <td key={cell.id} className="h-12 px-4 text-slate-600 dark:text-slate-300">{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>)}</tr>)}</tbody></table></div>}
      <footer className="flex items-center border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800"><span>{table.getRowModel().rows.length} of {tickets.data?.total ?? 0} tickets</span><div className="ml-auto flex gap-1"><Button variant="ghost" className="size-8 p-0" disabled aria-label="Previous page"><ChevronLeft size={16} /></Button><Button variant="ghost" className="size-8 p-0" disabled aria-label="Next page"><ChevronRight size={16} /></Button></div></footer>
    </section>
    <QuickCreateTicket open={createOpen} pending={create.isPending} error={create.error instanceof Error ? create.error.message : undefined} queues={queues.data ?? []} categories={categories.data ?? []} onClose={() => { if (!create.isPending) { setCreateOpen(false); create.reset() } }} onSubmit={async (input) => { await create.mutateAsync(input) }} />
  </div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) { return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div><span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span><h2 className="mt-3 font-semibold">Tickets could not be loaded</h2><p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p><Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button></div></div> }
function TicketTableSkeleton() { return <div aria-label="Loading tickets" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div> }
