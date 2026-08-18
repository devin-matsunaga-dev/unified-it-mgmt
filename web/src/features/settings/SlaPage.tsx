import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlarmClock, ArrowLeft, CalendarClock, Pencil, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { helpdeskApi, type TicketPriority, type TicketType } from '../../api/helpdesk'
import {
  businessDays, describeMinutes, describePolicyConditions, slaApi,
  type BusinessHoursCalendar, type SavePolicyInput, type SlaPolicy,
} from '../../api/sla'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { flattenCategories } from '../tickets/categoryFields'

const priorities: TicketPriority[] = ['Critical', 'High', 'Medium', 'Low']

/**
 * Service levels: the ordered list of rules a new ticket is matched against, and the calendars they
 * measure against.
 *
 * The two things worth saying out loud on this screen, because both surprise people, are that the
 * FIRST matching policy wins — so order is the feature, not decoration — and that editing a policy
 * reaches new tickets only, because a running clock keeps the target it started with.
 */
export function SlaPage() {
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<SlaPolicy | null>(null)
  const [creating, setCreating] = useState(false)
  const [calendarOpen, setCalendarOpen] = useState(false)
  const [dragging, setDragging] = useState<string | null>(null)

  const policies = useQuery({
    queryKey: ['sla-policies'],
    queryFn: slaApi.listPolicies,
    meta: { suppressErrorToast: true },
  })
  const calendars = useQuery({ queryKey: ['sla-calendars'], queryFn: slaApi.listCalendars })
  const categories = useQuery({ queryKey: ['ticket-categories'], queryFn: helpdeskApi.listCategories })

  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['sla-policies'] }),
      queryClient.invalidateQueries({ queryKey: ['sla-calendars'] }),
    ])
  }

  const save = useMutation({
    mutationFn: (input: SavePolicyInput) => editing
      ? slaApi.updatePolicy(editing.id, input)
      : slaApi.createPolicy(input),
    onSuccess: async (policy) => {
      await refresh()
      toast.success(`${policy.name} ${editing ? 'updated' : 'created'}`)
      setEditing(null)
      setCreating(false)
      save.reset()
    },
  })

  const remove = useMutation({
    mutationFn: (policy: SlaPolicy) => slaApi.deletePolicy(policy.id),
    onSuccess: async () => { await refresh(); toast.success('Policy deleted') },
    onError: (error: Error) => toast.error(error.message),
  })

  const reorder = useMutation({
    mutationFn: (policyIds: string[]) => slaApi.reorderPolicies(policyIds),
    onSuccess: async () => { await refresh() },
    onError: (error: Error) => toast.error(error.message),
  })

  const removeCalendar = useMutation({
    mutationFn: (calendar: BusinessHoursCalendar) => slaApi.deleteCalendar(calendar.id),
    onSuccess: async () => { await refresh(); toast.success('Calendar deleted') },
    onError: (error: Error) => toast.error(error.message),
  })

  const list = policies.data ?? []
  const categoryOptions = flattenCategories(categories.data ?? [])

  function onDropOn(target: SlaPolicy) {
    if (!dragging || dragging === target.id) return
    const order = list.map((policy) => policy.id).filter((id) => id !== dragging)
    order.splice(list.findIndex((policy) => policy.id === target.id), 0, dragging)
    reorder.mutate(order)
    setDragging(null)
  }

  return <div className="space-y-6">
    <Link to="/admin/settings" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to settings</Link>

    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[28px] font-bold">Service levels</h1>
        <p className="mt-1 text-sm text-slate-500">
          A new ticket takes the <strong>first</strong> policy below whose conditions it meets, so the order is
          the rule. Editing one reaches new tickets only — a clock already running keeps the target it started with.
        </p>
      </div>
      <Button className="sm:ml-auto" onClick={() => { save.reset(); setEditing(null); setCreating(true) }}>
        <Plus size={18} />New policy
      </Button>
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      {policies.isLoading ? <div aria-label="Loading policies" className="space-y-px p-4">{Array.from({ length: 4 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
        : policies.isError ? <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
            <h2 className="font-semibold">Policies could not be loaded</h2>
            <p className="mt-1 text-sm text-slate-500">{policies.error instanceof ApiError ? policies.error.message : 'Try again in a moment.'}</p>
            <Button className="mt-4" variant="secondary" onClick={() => void policies.refetch()}>Try again</Button>
          </div></div>
        : list.length === 0 ? <div className="grid min-h-64 place-items-center p-8 text-center"><div>
            <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><AlarmClock /></span>
            <h2 className="mt-3 font-semibold">No policies yet</h2>
            {/* Not an empty formality: with no policy, a new ticket gets no clock at all. */}
            <p className="mt-1 text-sm text-slate-500">Until one exists, no ticket is measured against anything.</p>
            <Button className="mt-4" onClick={() => { save.reset(); setCreating(true) }}>New policy</Button>
          </div></div>
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[900px] text-left text-sm">
              <thead><tr>
                {['#', 'Name', 'Applies to', 'Response', 'Resolution', 'Warn at', 'Calendar', 'State', ''].map((header) =>
                  <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {list.map((policy, index) => <tr key={policy.id}
                  draggable
                  onDragStart={(event) => { setDragging(policy.id); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move' }}
                  onDragOver={(event) => { if (dragging && dragging !== policy.id) event.preventDefault() }}
                  onDrop={(event) => { event.preventDefault(); onDropOn(policy) }}
                  onDragEnd={() => setDragging(null)}
                  className={cn('cursor-grab border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50',
                    dragging === policy.id && 'opacity-40',
                    !policy.isActive && 'text-slate-400')}>
                  <td className="h-12 px-4 tabular-nums text-slate-400">{index + 1}</td>
                  <td className="h-12 px-4 font-medium text-slate-900 dark:text-slate-100">{policy.name}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{describePolicyConditions(policy)}</td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{describeMinutes(policy.responseTargetMinutes)}</td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{describeMinutes(policy.resolutionTargetMinutes)}</td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{policy.warningPercent}%</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{policy.calendarName}</td>
                  <td className="h-12 px-4">
                    <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${policy.isActive
                      ? 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400'
                      : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}`}>
                      {policy.isActive ? 'Active' : 'Inactive'}
                    </span>
                  </td>
                  <td className="h-12 px-4 text-right whitespace-nowrap">
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" aria-label={`Edit ${policy.name}`}
                      onClick={() => { save.reset(); setCreating(false); setEditing(policy) }}>
                      <Pencil size={15} />Edit
                    </Button>
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" aria-label={`Delete ${policy.name}`}
                      disabled={remove.isPending}
                      onClick={() => {
                        if (window.confirm(`Delete ${policy.name}? A policy tickets have run against cannot be deleted — deactivate it instead.`)) {
                          remove.mutate(policy)
                        }
                      }}>
                      <Trash2 size={15} />Delete
                    </Button>
                  </td>
                </tr>)}
              </tbody>
            </table>
            <p className="border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800">
              Drag a row to change which policy is tried first. A policy with no conditions matches everything,
              so anything below it is never reached.
            </p>
          </div>}
    </section>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-center gap-3 border-b border-slate-200 px-4 py-3 dark:border-slate-800">
        <CalendarClock size={17} className="text-slate-400" />
        <h2 className="font-semibold">Business hours</h2>
        <Button variant="secondary" className="ml-auto h-9" onClick={() => setCalendarOpen(true)}>
          <Plus size={16} />New calendar
        </Button>
      </div>
      <table className="w-full text-left text-sm">
        <thead><tr>
          {['Name', 'Time zone', 'Days', 'Hours', 'Used by', ''].map((header) =>
            <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
        </tr></thead>
        <tbody>
          {(calendars.data ?? []).map((calendar) => <tr key={calendar.id} className="border-t border-slate-200 dark:border-slate-800">
            <td className="h-12 px-4 font-medium text-slate-900 dark:text-slate-100">{calendar.name}</td>
            <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{calendar.timeZoneId}</td>
            <td className="h-12 px-4 text-slate-600 dark:text-slate-300">
              {businessDays.filter((day) => (calendar.workingDays & day.value) !== 0).map((day) => day.label).join(' ') || '—'}
            </td>
            <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">
              {calendar.startTime.slice(0, 5)}–{calendar.endTime.slice(0, 5)}
            </td>
            <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">
              {calendar.policyCount} {calendar.policyCount === 1 ? 'policy' : 'policies'}
            </td>
            <td className="h-12 px-4 text-right">
              <Button variant="ghost" className="h-8 px-2 text-[13px]" aria-label={`Delete ${calendar.name}`}
                disabled={removeCalendar.isPending}
                onClick={() => { if (window.confirm(`Delete ${calendar.name}?`)) removeCalendar.mutate(calendar) }}>
                <Trash2 size={15} />Delete
              </Button>
            </td>
          </tr>)}
        </tbody>
      </table>
      {/* Editing is not offered: a calendar's hours are read live by every clock measuring against
          it, so changing them would move running tickets — the thing snapshotting exists to prevent. */}
      <p className="border-t border-slate-200 px-4 py-3 text-[13px] text-slate-500 dark:border-slate-800">
        A calendar's hours are read by every clock measuring against it, including tickets already running,
        so they cannot be edited. Add a new calendar and point policies at it instead.
      </p>
    </section>

    {(creating || editing) && <PolicyDialog
      key={editing?.id ?? 'new'}
      policy={editing}
      calendars={calendars.data ?? []}
      categories={categoryOptions}
      pending={save.isPending}
      error={save.error instanceof Error ? save.error.message : undefined}
      onClose={() => { if (!save.isPending) { setEditing(null); setCreating(false); save.reset() } }}
      onSubmit={(input) => save.mutate(input)} />}

    {calendarOpen && <CalendarDialog onClose={() => setCalendarOpen(false)} onSaved={async () => {
      setCalendarOpen(false)
      await refresh()
    }} />}
  </div>
}

function PolicyDialog({ policy, calendars, categories, pending, error, onClose, onSubmit }: {
  policy: SlaPolicy | null
  calendars: BusinessHoursCalendar[]
  categories: { id: string; name: string; depth: number }[]
  pending: boolean
  error?: string
  onClose: () => void
  onSubmit: (input: SavePolicyInput) => void
}) {
  const [form, setForm] = useState<SavePolicyInput>(policy
    ? {
      name: policy.name,
      responseTargetMinutes: policy.responseTargetMinutes,
      resolutionTargetMinutes: policy.resolutionTargetMinutes,
      warningPercent: policy.warningPercent,
      calendarId: policy.calendarId,
      priority: policy.priority,
      ticketType: policy.ticketType,
      categoryId: policy.categoryId,
      sortOrder: policy.sortOrder,
      isActive: policy.isActive,
    }
    : {
      name: '', responseTargetMinutes: 30, resolutionTargetMinutes: 480, warningPercent: 80,
      calendarId: calendars[0]?.id ?? '', priority: null, ticketType: null, categoryId: null,
      sortOrder: 0, isActive: true,
    })

  const set = <K extends keyof SavePolicyInput>(key: K, value: SavePolicyInput[K]) =>
    setForm((current) => ({ ...current, [key]: value }))

  const targetsInverted = form.resolutionTargetMinutes < form.responseTargetMinutes

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={policy ? `Edit ${policy.name}` : 'New policy'}>
    <form className="max-h-[90vh] w-full max-w-xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); onSubmit({ ...form, name: form.name.trim() }) }}>
      <h2 className="text-lg font-semibold">{policy ? `Edit ${policy.name}` : 'New policy'}</h2>

      <div className="mt-5">
        <label htmlFor="policy-name" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Name</label>
        <input id="policy-name" required maxLength={100} autoFocus className="input h-11"
          value={form.name} onChange={(event) => set('name', event.target.value)} />
      </div>

      <fieldset className="mt-5">
        <legend className="text-[13px] font-medium text-slate-600 dark:text-slate-300">Applies to</legend>
        <p className="mt-1 text-[13px] text-slate-500">
          Leave any of these as “Any” to stop asking about it. A policy with all three left open matches
          every ticket, which is what a catch-all at the bottom of the list is for.
        </p>
        <div className="mt-2 grid gap-3 sm:grid-cols-3">
          <div>
            <label htmlFor="policy-priority" className="mb-1.5 block text-[13px] text-slate-500">Priority</label>
            <select id="policy-priority" className="input h-11" value={form.priority ?? ''}
              onChange={(event) => set('priority', (event.target.value || null) as TicketPriority | null)}>
              <option value="">Any priority</option>
              {priorities.map((priority) => <option key={priority} value={priority}>{priority}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="policy-type" className="mb-1.5 block text-[13px] text-slate-500">Kind</label>
            <select id="policy-type" className="input h-11" value={form.ticketType ?? ''}
              onChange={(event) => set('ticketType', (event.target.value || null) as TicketType | null)}>
              <option value="">Any kind</option>
              <option value="Incident">Incidents</option>
              <option value="ServiceRequest">Service requests</option>
            </select>
          </div>
          <div>
            <label htmlFor="policy-category" className="mb-1.5 block text-[13px] text-slate-500">Category</label>
            <select id="policy-category" className="input h-11" value={form.categoryId ?? ''}
              onChange={(event) => set('categoryId', event.target.value || null)}>
              <option value="">Any category</option>
              {categories.map((category) => <option key={category.id} value={category.id}>
                {' '.repeat(category.depth * 2)}{category.name}
              </option>)}
            </select>
          </div>
        </div>
      </fieldset>

      <fieldset className="mt-5">
        <legend className="text-[13px] font-medium text-slate-600 dark:text-slate-300">Targets</legend>
        <div className="mt-2 grid gap-3 sm:grid-cols-3">
          <div>
            <label htmlFor="policy-response" className="mb-1.5 block text-[13px] text-slate-500">Response (minutes)</label>
            <input id="policy-response" type="number" min={1} max={525600} required className="input h-11"
              value={form.responseTargetMinutes}
              onChange={(event) => set('responseTargetMinutes', Number(event.target.value) || 0)} />
            <p className="mt-1 text-[12px] text-slate-400">{describeMinutes(form.responseTargetMinutes)}</p>
          </div>
          <div>
            <label htmlFor="policy-resolution" className="mb-1.5 block text-[13px] text-slate-500">Resolution (minutes)</label>
            <input id="policy-resolution" type="number" min={1} max={525600} required className="input h-11"
              value={form.resolutionTargetMinutes}
              onChange={(event) => set('resolutionTargetMinutes', Number(event.target.value) || 0)} />
            <p className="mt-1 text-[12px] text-slate-400">{describeMinutes(form.resolutionTargetMinutes)}</p>
          </div>
          <div>
            <label htmlFor="policy-warning" className="mb-1.5 block text-[13px] text-slate-500">Warn at (%)</label>
            <input id="policy-warning" type="number" min={1} max={99} required className="input h-11"
              value={form.warningPercent}
              onChange={(event) => set('warningPercent', Number(event.target.value) || 0)} />
          </div>
        </div>
        {targetsInverted && <p role="alert" className="mt-2 text-xs text-red-600">
          Resolution cannot be sooner than response.
        </p>}
      </fieldset>

      <div className="mt-5">
        <label htmlFor="policy-calendar" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Business hours</label>
        <select id="policy-calendar" required className="input h-11" value={form.calendarId}
          onChange={(event) => set('calendarId', event.target.value)}>
          {calendars.map((calendar) => <option key={calendar.id} value={calendar.id}>{calendar.name}</option>)}
        </select>
      </div>

      <label className="mt-5 flex items-center gap-2 text-[13px] font-medium text-slate-600 dark:text-slate-300">
        <input type="checkbox" className="size-4 rounded border-slate-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
          checked={form.isActive ?? true} onChange={(event) => set('isActive', event.target.checked)} />
        Active
      </label>
      <p className="mt-1.5 text-[13px] text-slate-500">
        An inactive policy is skipped when a ticket is matched. Tickets already measured against it keep running.
      </p>

      {policy && policy.ticketCount > 0 && <p className="mt-4 rounded-lg bg-slate-50 px-3 py-2 text-[13px] text-slate-600 dark:bg-slate-800 dark:text-slate-300">
        {policy.ticketCount} {policy.ticketCount === 1 ? 'ticket has' : 'tickets have'} run against this policy.
        They keep the targets they started with; this edit applies to new tickets.
      </p>}

      {error && <p role="alert" className="mt-4 text-xs text-red-600">{error}</p>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={pending || form.name.trim() === '' || form.calendarId === '' || targetsInverted}>
          {pending ? 'Saving…' : policy ? 'Save policy' : 'Create policy'}
        </Button>
      </div>
    </form>
  </div>
}

function CalendarDialog({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState('')
  const [timeZoneId, setTimeZoneId] = useState(
    Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC')
  const [days, setDays] = useState(31)
  const [startTime, setStartTime] = useState('09:00')
  const [endTime, setEndTime] = useState('17:00')

  const create = useMutation({
    mutationFn: () => slaApi.createCalendar({
      name: name.trim(), timeZoneId, workingDays: days,
      startTime: `${startTime}:00`, endTime: `${endTime}:00`,
    }),
    onSuccess: async () => { toast.success(`${name.trim()} created`); await onSaved() },
  })

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label="New calendar">
    <form className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); create.mutate() }}>
      <h2 className="text-lg font-semibold">New calendar</h2>

      <div className="mt-5 grid gap-4 sm:grid-cols-2">
        <div>
          <label htmlFor="calendar-name" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Name</label>
          <input id="calendar-name" required maxLength={100} autoFocus className="input h-11"
            value={name} onChange={(event) => setName(event.target.value)} />
        </div>
        <div>
          <label htmlFor="calendar-zone" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Time zone</label>
          {/* Prefilled from the browser: the common case is the desk the administrator is sitting at. */}
          <input id="calendar-zone" required maxLength={100} className="input h-11"
            value={timeZoneId} onChange={(event) => setTimeZoneId(event.target.value)} />
        </div>
      </div>

      <fieldset className="mt-4">
        <legend className="text-[13px] font-medium text-slate-600 dark:text-slate-300">Working days</legend>
        <div className="mt-2 flex flex-wrap gap-1">
          {businessDays.map((day) => {
            const on = (days & day.value) !== 0
            return <button key={day.value} type="button" aria-pressed={on} aria-label={day.label}
              onClick={() => setDays((current) => on ? current & ~day.value : current | day.value)}
              className={cn('h-9 w-12 rounded-lg border text-[13px] font-medium',
                on ? 'border-blue-600 bg-blue-600 text-white' : 'border-slate-200 text-slate-600 dark:border-slate-700 dark:text-slate-300')}>
              {day.label}
            </button>
          })}
        </div>
      </fieldset>

      <div className="mt-4 grid gap-4 sm:grid-cols-2">
        <div>
          <label htmlFor="calendar-start" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Opens</label>
          <input id="calendar-start" type="time" required className="input h-11"
            value={startTime} onChange={(event) => setStartTime(event.target.value)} />
        </div>
        <div>
          <label htmlFor="calendar-end" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Closes</label>
          <input id="calendar-end" type="time" required className="input h-11"
            value={endTime} onChange={(event) => setEndTime(event.target.value)} />
        </div>
      </div>

      {create.error instanceof Error && <p role="alert" className="mt-4 text-xs text-red-600">{create.error.message}</p>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={create.isPending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={create.isPending || name.trim() === '' || days === 0 || endTime <= startTime}>
          {create.isPending ? 'Saving…' : 'Create calendar'}
        </Button>
      </div>
    </form>
  </div>
}
