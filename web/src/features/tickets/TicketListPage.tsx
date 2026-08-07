import { flexRender, getCoreRowModel, getFilteredRowModel, getSortedRowModel, useReactTable, type ColumnDef, type ColumnFiltersState, type SortingState, type VisibilityState } from '@tanstack/react-table'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Columns3, Plus, Search } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { helpdeskApi, type CreateTicketInput, type Ticket } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { PriorityPill, StatusPill, formatLocal } from './ticketUi'
import { QuickCreateTicket } from './QuickCreateTicket'

const savedVisibilityKey = 'tickets.column-visibility'
const columns: ColumnDef<Ticket>[] = [
  { accessorKey: 'number', header: 'ID', cell: ({ row }) => <Link className="font-mono text-xs text-blue-600 hover:underline" to={`/tickets/${row.original.id}`}>#{row.original.number}</Link> },
  { accessorKey: 'title', header: 'Title', cell: ({ row }) => <Link className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100" to={`/tickets/${row.original.id}`}>{row.original.title}</Link> },
  { accessorKey: 'status', header: 'Status', cell: ({ getValue }) => <StatusPill status={getValue<string>()} /> },
  { accessorKey: 'priority', header: 'Priority', cell: ({ getValue }) => <PriorityPill priority={getValue<Ticket['priority']>()} /> },
  { accessorKey: 'categoryName', header: 'Category', cell: ({ getValue }) => getValue<string | null>() ?? 'Uncategorised' },
  { accessorKey: 'queueName', header: 'Queue', cell: ({ getValue }) => getValue<string | null>() ?? 'Unqueued' },
  { accessorKey: 'assignedTechnicianId', header: 'Assignee', cell: ({ getValue }) => getValue<string | null>() ?? 'Unassigned' },
  { accessorKey: 'updatedAt', header: 'Updated', cell: ({ getValue }) => <span className="whitespace-nowrap text-slate-500">{formatLocal(getValue<string>())}</span> },
]

export function TicketListPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [createOpen, setCreateOpen] = useState(false)
  const [sorting, setSorting] = useState<SortingState>([{ id: 'updatedAt', desc: true }])
  const [filters, setFilters] = useState<ColumnFiltersState>([])
  const [globalFilter, setGlobalFilter] = useState('')
  const [columnMenu, setColumnMenu] = useState(false)
  const [visibility, setVisibility] = useState<VisibilityState>(() => {
    try { return JSON.parse(localStorage.getItem(savedVisibilityKey) ?? '{}') as VisibilityState } catch { return {} }
  })
  const tickets = useQuery({ queryKey: ['tickets'], queryFn: helpdeskApi.listTickets })
  const queues = useQuery({ queryKey: ['queues'], queryFn: helpdeskApi.listQueues })
  const categories = useQuery({ queryKey: ['ticket-categories'], queryFn: helpdeskApi.listCategories, staleTime: 0, refetchOnMount: 'always' })
  const create = useMutation({
    mutationFn: (input: CreateTicketInput) => helpdeskApi.createTicket(input),
    onSuccess: async (ticket) => { await queryClient.invalidateQueries({ queryKey: ['tickets'] }); setCreateOpen(false); toast.success(`${ticket.number} created`); navigate(`/tickets/${ticket.id}`) },
  })
  const table = useReactTable({
    data: tickets.data?.items ?? [], columns, state: { sorting, columnFilters: filters, globalFilter, columnVisibility: visibility },
    onSortingChange: setSorting, onColumnFiltersChange: setFilters, onGlobalFilterChange: setGlobalFilter,
    onColumnVisibilityChange: (updater) => setVisibility((current) => { const next = typeof updater === 'function' ? updater(current) : updater; localStorage.setItem(savedVisibilityKey, JSON.stringify(next)); return next }),
    getCoreRowModel: getCoreRowModel(), getSortedRowModel: getSortedRowModel(), getFilteredRowModel: getFilteredRowModel(),
  })
  const statuses = useMemo(() => Array.from(new Set(tickets.data?.items.map((ticket) => ticket.status) ?? [])).sort(), [tickets.data])

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end"><div><h1 className="text-[28px] font-bold">Tickets</h1><p className="mt-1 text-sm text-slate-500">Triage, assign, and resolve service work.</p></div><Button className="sm:ml-auto" onClick={() => setCreateOpen(true)}><Plus size={18} />New ticket</Button></div>
    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="flex h-10 min-w-60 flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700"><Search size={17} /><span className="sr-only">Search tickets</span><input value={globalFilter} onChange={(event) => setGlobalFilter(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search ID, title, queue, assignee…" /></label>
        <select aria-label="Filter by status" className="input w-auto min-w-36" value={(filters.find((filter) => filter.id === 'status')?.value as string) ?? ''} onChange={(event) => table.getColumn('status')?.setFilterValue(event.target.value || undefined)}><option value="">All statuses</option>{statuses.map((status) => <option key={status}>{status}</option>)}</select>
        <div className="relative"><Button variant="secondary" onClick={() => setColumnMenu((value) => !value)} aria-expanded={columnMenu}><Columns3 size={17} />Columns</Button>{columnMenu && <div className="absolute right-0 top-12 z-10 w-48 rounded-lg border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-700 dark:bg-slate-800">{table.getAllLeafColumns().map((column) => <label key={column.id} className="flex h-9 items-center gap-2 rounded px-2 text-sm hover:bg-slate-50 dark:hover:bg-slate-700"><input type="checkbox" checked={column.getIsVisible()} onChange={column.getToggleVisibilityHandler()} />{typeof column.columnDef.header === 'string' ? column.columnDef.header : column.id}</label>)}</div>}</div>
      </div>
      {tickets.isLoading ? <TicketTableSkeleton /> : tickets.isError ? <ErrorState error={tickets.error} retry={() => void tickets.refetch()} /> : table.getRowModel().rows.length === 0 ? <div className="grid min-h-64 place-items-center p-8 text-center"><div><span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600"><Search /></span><h2 className="mt-3 font-semibold">No matching tickets</h2><p className="mt-1 text-sm text-slate-500">Adjust the filters or create a new ticket.</p><Button className="mt-4" onClick={() => setCreateOpen(true)}>Create ticket</Button></div></div> : <div className="overflow-x-auto"><table className="w-full min-w-[900px] text-left text-sm"><thead><tr>{table.getHeaderGroups()[0].headers.map((header) => <th key={header.id} className="h-11 px-4 text-[13px] font-medium text-slate-500"><button className="inline-flex items-center gap-1" onClick={header.column.getToggleSortingHandler()}>{flexRender(header.column.columnDef.header, header.getContext())}{header.column.getCanSort() && (header.column.getIsSorted() === 'asc' ? <ArrowUp size={14} /> : header.column.getIsSorted() === 'desc' ? <ArrowDown size={14} /> : <ArrowUpDown size={14} />)}</button></th>)}</tr></thead><tbody>{table.getRowModel().rows.map((row) => <tr key={row.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">{row.getVisibleCells().map((cell) => <td key={cell.id} className="h-12 px-4 text-slate-600 dark:text-slate-300">{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>)}</tr>)}</tbody></table></div>}
      <footer className="flex items-center border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800"><span>{table.getFilteredRowModel().rows.length} of {tickets.data?.total ?? 0} tickets</span><div className="ml-auto flex gap-1"><Button variant="ghost" className="size-8 p-0" disabled aria-label="Previous page"><ChevronLeft size={16} /></Button><Button variant="ghost" className="size-8 p-0" disabled aria-label="Next page"><ChevronRight size={16} /></Button></div></footer>
    </section>
    <QuickCreateTicket open={createOpen} pending={create.isPending} error={create.error instanceof Error ? create.error.message : undefined} queues={queues.data ?? []} categories={categories.data ?? []} onClose={() => { if (!create.isPending) { setCreateOpen(false); create.reset() } }} onSubmit={async (input) => { await create.mutateAsync(input) }} />
  </div>
}

function ErrorState({ error, retry }: { error: Error; retry: () => void }) { return <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div><span className="mx-auto grid size-12 place-items-center rounded-full bg-red-50 text-red-600">!</span><h2 className="mt-3 font-semibold">Tickets could not be loaded</h2><p className="mt-1 text-sm text-slate-500">{error instanceof ApiError ? error.message : 'Try again in a moment.'}</p><Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button></div></div> }
function TicketTableSkeleton() { return <div aria-label="Loading tickets" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div> }
