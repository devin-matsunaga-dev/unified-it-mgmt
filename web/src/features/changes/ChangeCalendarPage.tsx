import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CalendarClock, ChevronLeft, ChevronRight, Plus, ShieldCheck, Wrench } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi } from '../../api/assets'
import { ApiError } from '../../api/client'
import { changesApi, type ChangeStatus } from '../../api/changes'
import { monitoringApi } from '../../api/monitoring'
import { Button } from '../../components/ui/Button'
import {
  addMonths,
  fromLocalInput,
  monthGrid,
  monthRange,
  placeEntries,
  toLocalInput,
  type CalendarEntry,
} from './changeCalendar'
import { ChangeStatusPill, WindowStatusPill, changeStatuses, coverageSummary } from './changeUi'

/**
 * The maintenance calendar: what is planned, what was agreed, and what is being muted right now.
 *
 * Changes and the maintenance windows they opened are drawn on one grid rather than on two screens,
 * because the question somebody brings to a calendar is "is anything happening to this estate on
 * Thursday" and the honest answer includes both halves. The two are read from their own modules'
 * endpoints and joined here by change id — neither module reads the other's schema.
 */
export function ChangeCalendarPage() {
  const [month, setMonth] = useState(() => {
    const now = new Date()
    return new Date(now.getFullYear(), now.getMonth(), 1)
  })
  const [creating, setCreating] = useState(false)
  const [status, setStatus] = useState<ChangeStatus | ''>('')
  const queryClient = useQueryClient()

  const { from, to } = monthRange(month)
  const range = { from: from.toISOString(), to: to.toISOString() }

  const changes = useQuery({
    queryKey: ['changes', range, status],
    queryFn: () => changesApi.list({
      ...range,
      statuses: status ? [status] : undefined,
      pageSize: 200,
    }),
  })

  const windows = useQuery({
    queryKey: ['maintenance-windows', range],
    queryFn: () => monitoringApi.listMaintenanceWindows({ ...range, pageSize: 200 }),
  })

  const days = useMemo(
    () => placeEntries(
      monthGrid(month, new Date()),
      changes.data?.items ?? [],
      windows.data?.items ?? []),
    [month, changes.data, windows.data])

  const items = changes.data?.items ?? []
  const awaiting = items.filter((change) => change.status === 'Submitted').length
  const muting = (windows.data?.items ?? []).filter((window) => window.status === 'InProgress').length

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[28px] font-bold">Changes</h1>
        <p className="mt-1 text-sm text-slate-500">
          Planned work on the estate. Approving a change opens a maintenance window over the items it
          covers, so the alerts the work itself causes are withheld for exactly as long as it was agreed
          to take.
        </p>
      </div>
      <div className="flex flex-wrap gap-2 sm:ml-auto">
        <Button onClick={() => setCreating(true)}><Plus size={18} />New change</Button>
      </div>
    </div>

    <div className="grid gap-4 sm:grid-cols-3">
      <Kpi label="Changes this month" value={changes.isSuccess ? items.length : undefined}
        tone="text-blue-600 bg-blue-50 dark:bg-blue-500/15" icon={<CalendarClock size={20} />} />
      <Kpi label="Waiting for a decision" value={changes.isSuccess ? awaiting : undefined}
        tone="text-amber-600 bg-amber-50 dark:bg-amber-500/15" icon={<ShieldCheck size={20} />} />
      <Kpi label="Muting right now" value={windows.isSuccess ? muting : undefined}
        tone="text-slate-600 bg-slate-100 dark:bg-slate-500/15" icon={<Wrench size={20} />} />
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <div className="flex items-center gap-1">
          <Button variant="secondary" className="size-9 p-0" aria-label="Previous month"
            onClick={() => setMonth(addMonths(month, -1))}><ChevronLeft size={18} /></Button>
          <Button variant="secondary" className="size-9 p-0" aria-label="Next month"
            onClick={() => setMonth(addMonths(month, 1))}><ChevronRight size={18} /></Button>
        </div>
        <h2 className="font-semibold" data-testid="calendar-month">
          {new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' }).format(month)}
        </h2>
        <Button variant="secondary" onClick={() => {
          const now = new Date()
          setMonth(new Date(now.getFullYear(), now.getMonth(), 1))
        }}>Today</Button>
        <select aria-label="Filter by status" className="input ml-auto w-auto min-w-44" value={status}
          onChange={(event) => setStatus(event.target.value as ChangeStatus | '')}>
          <option value="">Every status</option>
          {changeStatuses.map((option) => <option key={option} value={option}>{option}</option>)}
        </select>
      </div>

      {changes.isError || windows.isError
        ? <ErrorState
            error={(changes.error ?? windows.error) as unknown}
            retry={() => { void changes.refetch(); void windows.refetch() }}
          />
        : changes.isPending || windows.isPending
          ? <div aria-label="Loading calendar" className="grid grid-cols-7 gap-px bg-slate-200 p-4 dark:bg-slate-800">
              {Array.from({ length: 35 }, (_, index) =>
                <div key={index} className="h-24 animate-pulse bg-slate-100 dark:bg-slate-900" />)}
            </div>
          : <CalendarGrid days={days} />}
    </section>

    {creating && <NewChangeDialog
      onClose={() => setCreating(false)}
      onCreated={async () => {
        setCreating(false)
        await queryClient.invalidateQueries({ queryKey: ['changes'] })
      }}
    />}
  </div>
}

const weekdays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']

function CalendarGrid({ days }: { days: ReturnType<typeof placeEntries> }) {
  return <div className="overflow-x-auto p-4">
    <div className="min-w-[840px]">
      <div className="grid grid-cols-7 gap-2 pb-2">
        {weekdays.map((day) =>
          <div key={day} className="text-[13px] font-medium text-slate-500">{day}</div>)}
      </div>
      <div className="grid grid-cols-7 gap-2">
        {days.map((day) => <div
          key={day.date.toISOString()}
          className={[
            'min-h-24 rounded-lg border p-2',
            day.inMonth
              ? 'border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900'
              : 'border-slate-100 bg-slate-50 dark:border-slate-800/60 dark:bg-slate-900/40',
            day.isToday ? 'ring-2 ring-blue-500' : '',
          ].join(' ')}
        >
          <div className={day.inMonth
            ? 'text-[13px] font-medium tabular-nums text-slate-900 dark:text-slate-100'
            : 'text-[13px] tabular-nums text-slate-400'}>
            {day.date.getDate()}
          </div>
          <ul className="mt-1 space-y-1">
            {day.entries.map((entry) => <li key={`${entry.kind}-${entry.id}`}>
              <EntryChip entry={entry} />
            </li>)}
          </ul>
        </div>)}
      </div>
    </div>
  </div>
}

/**
 * A window is rendered as a chip that is not a link, because a maintenance window has no page of its own
 * — the thing worth opening is the change that opened it, and a window created by hand has nothing to
 * open at all. Saying which is which matters: one is a plan, the other is alerting actually being held.
 */
function EntryChip({ entry }: { entry: CalendarEntry }) {
  if (entry.kind === 'change') {
    const { change } = entry
    return <Link
      to={`/changes/${change.id}`}
      className="block truncate rounded border border-slate-200 bg-slate-50 px-1.5 py-0.5 text-xs hover:border-blue-300 hover:bg-blue-50 dark:border-slate-700 dark:bg-slate-800 dark:hover:bg-slate-700"
      title={`${change.number} — ${change.title}`}
    >
      <span className="mr-1 text-slate-500">{change.number}</span>
      <span className="text-slate-900 dark:text-slate-100">{change.title}</span>
    </Link>
  }

  const { window } = entry
  return <span
    className="block truncate rounded border border-amber-200 bg-amber-50 px-1.5 py-0.5 text-xs text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300"
    title={`${window.name} — alerts withheld`}
  >
    <Wrench size={12} className="mr-1 inline" />{window.name}
  </span>
}

function Kpi({ label, value, tone, icon }: {
  label: string
  value: number | undefined
  tone: string
  icon: React.ReactNode
}) {
  return <div className="flex items-center gap-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <span className={`grid size-10 place-items-center rounded-full ${tone}`}>{icon}</span>
    <div>
      <p className="text-[13px] text-slate-500">{label}</p>
      {/* "Unavailable" rather than 0 when the read failed: a zero is a claim about the estate (WP-2.11). */}
      <p className="text-[30px] font-bold leading-tight tabular-nums">{value ?? '—'}</p>
    </div>
  </div>
}

function ErrorState({ error, retry }: { error: unknown; retry: () => void }) {
  return <div className="p-8 text-center">
    <p className="text-sm text-slate-600 dark:text-slate-300">
      {error instanceof ApiError ? error.message : 'The calendar could not be loaded.'}
    </p>
    <Button variant="secondary" className="mt-3" onClick={retry}>Try again</Button>
  </div>
}

/**
 * Raising a change. Defaults to a slot starting now and ending in an hour, because the overwhelmingly
 * common case is somebody about to do the work — and a form that opened on an empty date field would ask
 * them to type today's date to say "now".
 */
function NewChangeDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => Promise<void> }) {
  const navigate = useNavigate()
  const now = new Date()
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [startsAt, setStartsAt] = useState(toLocalInput(now))
  const [endsAt, setEndsAt] = useState(toLocalInput(new Date(now.getTime() + 3_600_000)))
  const [ciIds, setCiIds] = useState<string[]>([])
  const [includeDependents, setIncludeDependents] = useState(false)
  const [search, setSearch] = useState('')

  const cis = useQuery({
    queryKey: ['cis', 'change-picker', search],
    queryFn: () => assetsApi.listCis({ search: search || undefined, pageSize: 25 }),
  })

  const create = useMutation({
    mutationFn: () => changesApi.create({
      title,
      description,
      plannedStartAt: fromLocalInput(startsAt),
      plannedEndAt: fromLocalInput(endsAt),
      ciIds,
      includeDependents,
    }),
    onSuccess: async (change) => {
      await onCreated()
      toast.success(`${change.number} raised.`)
      navigate(`/changes/${change.id}`)
    },
    onError: (error) => toast.error(error instanceof ApiError ? error.message : 'The change could not be raised.'),
  })

  const fieldError = (field: string) =>
    create.error instanceof ApiError ? create.error.errors?.[field]?.[0] : undefined

  return <div className="fixed inset-0 z-40 grid place-items-center bg-slate-950/40 p-4">
    <div role="dialog" aria-label="New change" className="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-lg font-semibold">New change</h2>
      <p className="mt-1 text-sm text-slate-500">
        A draft to begin with. It mutes nothing until somebody else approves it.
      </p>

      <div className="mt-4 space-y-4">
        <label className="block">
          <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Title</span>
          <input className="input" value={title} onChange={(event) => setTitle(event.target.value)} />
          <FieldError message={fieldError('Title')} />
        </label>

        <label className="block">
          <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">What is being done</span>
          <textarea className="input min-h-24" value={description} onChange={(event) => setDescription(event.target.value)} />
          <FieldError message={fieldError('Description')} />
        </label>

        <div className="grid gap-4 sm:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Starts</span>
            <input type="datetime-local" className="input" value={startsAt} onChange={(event) => setStartsAt(event.target.value)} />
          </label>
          <label className="block">
            <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Ends</span>
            <input type="datetime-local" className="input" value={endsAt} onChange={(event) => setEndsAt(event.target.value)} />
            <FieldError message={fieldError('PlannedEndAt')} />
          </label>
        </div>

        <div>
          <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">
            What it disturbs
          </span>
          <input
            className="input"
            placeholder="Search configuration items"
            aria-label="Search configuration items"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <FieldError message={fieldError('CiIds')} />
          <ul className="mt-2 max-h-48 divide-y divide-slate-200 overflow-y-auto rounded-lg border border-slate-200 dark:divide-slate-800 dark:border-slate-800">
            {(cis.data?.items ?? []).map((ci) => <li key={ci.id}>
              <label className="flex items-center gap-3 px-3 py-2 text-sm hover:bg-slate-50 dark:hover:bg-slate-800/50">
                <input
                  type="checkbox"
                  className="size-4 rounded border-slate-300"
                  checked={ciIds.includes(ci.id)}
                  onChange={(event) => setCiIds((current) => event.target.checked
                    ? [...current, ci.id]
                    : current.filter((id) => id !== ci.id))}
                />
                <span className="text-slate-900 dark:text-slate-100">{ci.name}</span>
                <span className="ml-auto text-xs text-slate-500">{ci.type}</span>
              </label>
            </li>)}
          </ul>
          <p className="mt-1 text-xs text-slate-500">{ciIds.length} selected</p>
        </div>

        <label className="flex items-start gap-3 text-sm">
          <input
            type="checkbox"
            className="mt-0.5 size-4 rounded border-slate-300"
            checked={includeDependents}
            onChange={(event) => setIncludeDependents(event.target.checked)}
          />
          <span>
            <span className="font-medium text-slate-900 dark:text-slate-100">Also cover what depends on these</span>
            <span className="mt-0.5 block text-xs text-slate-500">
              Worked out when the change is approved, from the dependency graph as it stands then. Rebooting
              a switch usually disturbs everything hanging off it.
            </span>
          </span>
        </label>
      </div>

      <div className="mt-6 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button disabled={create.isPending} onClick={() => create.mutate()}>
          {create.isPending ? 'Raising…' : 'Raise change'}
        </Button>
      </div>
    </div>
  </div>
}

/**
 * Kept outside the `<label>` deliberately: a hint inside one becomes part of the field's accessible name,
 * so a screen reader would read the error as the field's title (WP-5.7's bug, and its note to grep for).
 */
function FieldError({ message }: { message?: string }) {
  return message ? <p className="mt-1 text-xs text-red-600">{message}</p> : null
}
