import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Wrench } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { changesApi, type Change, type ChangeStatus } from '../../api/changes'
import { monitoringApi } from '../../api/monitoring'
import { Button } from '../../components/ui/Button'
import { formatLocal } from '../tickets/ticketUi'
import { formatPeriod } from './changeCalendar'
import { ChangeStatusPill, WindowStatusPill, changeActionLabel, coverageSummary } from './changeUi'

/**
 * One change: what it covers, where it stands, and — once approved — the maintenance window it opened.
 *
 * The window is read from Monitoring's own endpoint and matched by change id. That is the whole shape of
 * WP-5.8 on one screen: Assets holds the agreement, Monitoring holds the consequence, and nothing here
 * reaches into either module's tables to say so.
 */
export function ChangeDetailPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()

  const change = useQuery({ queryKey: ['changes', id], queryFn: () => changesApi.get(id) })

  // Only once approved: before that there is nothing to find, and asking would make every draft page
  // carry a request whose only possible answer is "no".
  const windows = useQuery({
    queryKey: ['maintenance-windows', 'for-change', id],
    queryFn: () => monitoringApi.listMaintenanceWindows({ pageSize: 200 }),
    enabled: change.data?.status === 'Approved',
  })

  if (change.isPending) {
    return <div aria-label="Loading change" className="space-y-4">
      {[0, 1, 2].map((index) =>
        <div key={index} className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />)}
    </div>
  }

  if (change.isError) {
    return <div className="rounded-xl border border-slate-200 bg-white p-8 text-center dark:border-slate-800 dark:bg-slate-900">
      <p className="text-sm text-slate-600 dark:text-slate-300">
        {change.error instanceof ApiError ? change.error.message : 'This change could not be loaded.'}
      </p>
      <Button variant="secondary" className="mt-3" onClick={() => void change.refetch()}>Try again</Button>
    </div>
  }

  const item = change.data
  const window = windows.data?.items.find((candidate) => candidate.changeRequestId === item.id)

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['changes'] })
    await queryClient.invalidateQueries({ queryKey: ['maintenance-windows'] })
  }

  return <div className="space-y-6">
    <div>
      <Link to="/changes" className="inline-flex items-center gap-1 text-[13px] text-slate-500 hover:text-blue-600">
        <ArrowLeft size={14} />Back to the calendar
      </Link>
      <div className="mt-2 flex flex-wrap items-center gap-3">
        <h1 className="text-[28px] font-bold">{item.title}</h1>
        <ChangeStatusPill status={item.status} />
      </div>
      <p className="mt-1 text-sm text-slate-500">
        {item.number} · raised by {item.requestedByName} on {formatLocal(item.requestedAt)}
      </p>
    </div>

    <Actions change={item} onMoved={refresh} />

    <div className="grid gap-6 lg:grid-cols-3">
      <section className="rounded-xl border border-slate-200 bg-white p-6 lg:col-span-2 dark:border-slate-800 dark:bg-slate-900">
        <h2 className="font-semibold">What is being done</h2>
        <p className="mt-2 whitespace-pre-wrap text-sm text-slate-600 dark:text-slate-300">{item.description}</p>

        <h3 className="mt-6 font-semibold">
          What it disturbs
          <span className="ml-2 text-[13px] font-normal text-slate-500">
            {coverageSummary(item.ciCount, item.dependentCount)}
          </span>
        </h3>
        {item.cis && item.cis.length > 0
          ? <ul className="mt-2 divide-y divide-slate-200 dark:divide-slate-800">
              {item.cis.map((ci) => <li key={ci.ciId} className="flex items-center gap-3 py-2 text-sm">
                <Link to={`/assets/${ci.ciId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">
                  {ci.name ?? 'A configuration item that no longer exists'}
                </Link>
                <span className="text-xs text-slate-500">{ci.type}</span>
                {ci.isDependent && <span className="rounded bg-slate-100 px-1.5 py-0.5 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                  depends on it
                </span>}
                <span className="ml-auto text-xs text-slate-500">{ci.lifecycleState}</span>
              </li>)}
            </ul>
          : <p className="mt-2 text-sm text-slate-500">
              Nothing is named yet. A change has to cover at least one configuration item before it can be
              submitted.
            </p>}
      </section>

      <div className="space-y-6">
        <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Planned slot</h2>
          <p className="mt-2 text-sm text-slate-600 dark:text-slate-300">
            {formatPeriod(item.plannedStartAt, item.plannedEndAt)}
          </p>
          <p className="mt-2 text-xs text-slate-500">
            {item.includeDependents
              ? 'Dependents are covered too, worked out from the dependency graph at approval.'
              : 'Only the items named above are covered.'}
          </p>
        </section>

        {item.decidedAt && <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Decision</h2>
          <p className="mt-2 text-sm text-slate-600 dark:text-slate-300">
            {item.status} by {item.decidedByName} on {formatLocal(item.decidedAt)}
          </p>
          {item.decisionNote && <p className="mt-2 whitespace-pre-wrap text-sm text-slate-600 dark:text-slate-300">
            {item.decisionNote}
          </p>}
        </section>}

        {item.status === 'Approved' && <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="flex items-center gap-2 font-semibold"><Wrench size={18} />Maintenance window</h2>
          {windows.isPending
            ? <div aria-label="Loading maintenance window" className="mt-2 h-10 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />
            : windows.isError
              // An unreadable answer is not an empty one — WP-2.11's rule, and here it matters twice
              // over, because "no window" reads as "your estate is not muted".
              ? <p className="mt-2 text-sm text-slate-500">
                  Monitoring could not be reached, so whether a window exists is unknown.
                </p>
              : window
                ? <div className="mt-2 space-y-2 text-sm">
                    <div className="flex items-center gap-2">
                      <WindowStatusPill status={window.status} />
                      <span className="text-slate-600 dark:text-slate-300">
                        {window.deviceIds.length} device{window.deviceIds.length === 1 ? '' : 's'}
                      </span>
                    </div>
                    <p className="text-slate-600 dark:text-slate-300">
                      {formatPeriod(window.startsAt, window.endsAt)}
                    </p>
                    <p className="text-xs text-slate-500">
                      Alerts about these devices are withheld while the window is open. They are still
                      recorded, so what happened during the work stays readable afterwards.
                    </p>
                  </div>
                : <p className="mt-2 text-sm text-slate-500">
                    No window was opened, because nothing this change covers is monitored. Most of a CMDB
                    is not — laptops, licences, logical services — so this is ordinary rather than wrong.
                  </p>}
        </section>}
      </div>
    </div>
  </div>
}

/**
 * The buttons the server says are available. `nextStatuses` comes off the change itself rather than from
 * a copy of the workflow in the browser — WP-5.7 duplicated its problem workflow here and left a note
 * that the failure mode is a button that is never offered and nobody reports.
 */
function Actions({ change, onMoved }: { change: Change; onMoved: () => Promise<void> }) {
  const [note, setNote] = useState('')

  const move = useMutation({
    mutationFn: (target: ChangeStatus) => changesApi.transition(change.id, target, note),
    onSuccess: async (updated) => {
      setNote('')
      await onMoved()
      toast.success(updated.status === 'Approved'
        ? `${updated.number} approved. A maintenance window has been asked for.`
        : `${updated.number} is now ${updated.status}.`)
    },
    onError: (error) => toast.error(
      error instanceof ApiError ? error.message : 'That change could not be moved.'),
  })

  if (change.nextStatuses.length === 0) {
    return <p className="rounded-xl border border-slate-200 bg-white p-4 text-sm text-slate-500 dark:border-slate-800 dark:bg-slate-900">
      This change is {change.status.toLowerCase()} and finished. Raise a new one if the work needs to
      happen again.
    </p>
  }

  return <section className="rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900">
    <div className="flex flex-wrap items-center gap-2">
      {change.nextStatuses.map((target) => <Button
        key={target}
        variant={target === 'Approved' ? 'primary' : 'secondary'}
        disabled={move.isPending}
        onClick={() => move.mutate(target)}
      >{changeActionLabel(target)}</Button>)}
      <input
        className="input ml-auto w-auto min-w-64"
        placeholder="Note (optional)"
        aria-label="Decision note"
        value={note}
        onChange={(event) => setNote(event.target.value)}
      />
    </div>
    {change.status === 'Submitted' && <p className="mt-2 text-xs text-slate-500">
      A change has to be approved by somebody other than the person who raised it.
    </p>}
  </section>
}
