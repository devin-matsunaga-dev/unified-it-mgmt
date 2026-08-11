import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BellRing, Check, CircleAlert, ShieldCheck, TriangleAlert, Waves } from 'lucide-react'
import { useCallback, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { monitoringApi, type Alert, type AlertFilter, type AlertPage } from '../../api/monitoring'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { LiveIndicator } from './LiveIndicator'
import { MonitoringTabs } from './MonitoringTabs'
import { SeverityPill, formatAge, formatLocal, severityTone } from './severity'
import { useMonitoringHub } from './useMonitoringHub'

const filters: { key: string; label: string; filter: AlertFilter }[] = [
  { key: 'open', label: 'Open', filter: { status: 'Open' } },
  { key: 'unacknowledged', label: 'Unacknowledged', filter: { status: 'Open', acknowledged: false } },
  { key: 'critical', label: 'Critical', filter: { status: 'Open', severity: 'Critical' } },
  { key: 'cleared', label: 'Cleared', filter: { status: 'Cleared' } },
]

/**
 * What is wrong right now, worst first, kept live by the hub. Acknowledging is the one write on this
 * screen; everything else is a read of the durable alert row.
 */
export function AlertBoardPage() {
  const queryClient = useQueryClient()
  const [selected, setSelected] = useState('open')
  const filter = filters.find((entry) => entry.key === selected)?.filter ?? filters[0].filter
  const queryKey = useMemo(() => ['monitoring', 'alerts', filter], [filter])

  const alerts = useQuery({
    queryKey,
    queryFn: () => monitoringApi.listAlerts({ ...filter, pageSize: 200 }),
    placeholderData: keepPreviousData,
  })

  const onAlertChanged = useCallback((alert: Alert) => {
    // A push carries the whole row, so the list can be patched rather than refetched. An alert that
    // no longer belongs under the current filter is dropped — a cleared alert has to leave the open
    // board without waiting for a refresh, or "recovery clears the board" is not observable.
    queryClient.setQueriesData<AlertPage>({ queryKey: ['monitoring', 'alerts'] }, (current) => {
      if (!current) return current
      const others = current.items.filter((item) => item.id !== alert.id)
      const belongs = current.items.some((item) => item.id === alert.id) || alert.status === 'Open'
      const items = belongs && matches(alert, filter) ? [alert, ...others] : others
      return { ...current, items: sortAlerts(items) }
    })
    // The counts are the server's tally over the whole estate, so they are re-read rather than
    // recomputed here from a page that may not hold every alert.
    void queryClient.invalidateQueries({ queryKey: ['monitoring', 'alerts'], refetchType: 'none' })
  }, [queryClient, filter])

  const onResync = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['monitoring'] })
  }, [queryClient])

  const events = useMemo(() => ({ onAlertChanged, onResync }), [onAlertChanged, onResync])
  const hub = useMonitoringHub(events)

  const acknowledge = useMutation({
    mutationFn: (id: string) => monitoringApi.acknowledgeAlert(id),
    onSuccess: (alert) => {
      // The server pushes this to every other board; patching here too means the operator who
      // pressed the button does not wait for their own round trip.
      onAlertChanged(alert)
      toast.success('Acknowledged', { description: alert.summary })
    },
    onError: (error: Error) => toast.error('Could not acknowledge', { description: error.message }),
  })

  const counts = alerts.data?.counts
  const items = alerts.data?.items ?? []

  return <div className="space-y-6">
    <MonitoringTabs right={<LiveIndicator status={hub} />} />

    <div role="group" aria-label="Alert counts" className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <CountTile label="Open alerts" value={counts?.open} icon={BellRing} tone="bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400" loading={alerts.isLoading} failed={alerts.isError} />
      <CountTile label="Critical" value={counts?.critical} icon={CircleAlert} tone="bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400" loading={alerts.isLoading} failed={alerts.isError} />
      <CountTile label="Warning" value={counts?.warning} icon={TriangleAlert} tone="bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400" loading={alerts.isLoading} failed={alerts.isError} />
      <CountTile label="Unacknowledged" value={counts?.unacknowledged} icon={ShieldCheck} tone="bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400" loading={alerts.isLoading} failed={alerts.isError} />
    </div>

    <div className="flex flex-wrap gap-2">
      {filters.map((entry) => <button key={entry.key} type="button" aria-pressed={selected === entry.key}
        onClick={() => setSelected(entry.key)}
        className={cn('rounded-lg border px-3 py-1.5 text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
          selected === entry.key
            ? 'border-blue-600 bg-blue-600 text-white'
            : 'border-slate-200 bg-white text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300 dark:hover:bg-slate-800')}>
        {entry.label}
      </button>)}
    </div>

    <div className="overflow-hidden rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      {alerts.isLoading
        ? <div className="space-y-2 p-5">
          {[0, 1, 2, 3].map((key) => <div key={key} aria-label="Loading alerts" className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
        </div>
        : items.length === 0
          ? <div className="p-10 text-center">
            <span className="mx-auto mb-3 grid size-12 place-items-center rounded-full bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400"><ShieldCheck size={22} /></span>
            <p className="text-sm text-slate-500">
              {selected === 'cleared' ? 'Nothing has cleared yet.' : 'Nothing is alerting. The estate is quiet.'}
            </p>
          </div>
          : <div className="overflow-x-auto">
            <table className="w-full min-w-[880px] text-sm">
              <thead>
                <tr className="border-b border-slate-200 text-left text-[13px] font-medium text-slate-500 dark:border-slate-800">
                  <th className="px-5 py-3">Severity</th>
                  <th className="px-5 py-3">Alert</th>
                  <th className="px-5 py-3">Asset</th>
                  <th className="px-5 py-3">Raised</th>
                  <th className="px-5 py-3">Acknowledged</th>
                  <th className="px-5 py-3" />
                </tr>
              </thead>
              <tbody>
                {items.map((alert) => <AlertRow key={alert.id} alert={alert}
                  onAcknowledge={() => acknowledge.mutate(alert.id)}
                  acknowledging={acknowledge.isPending && acknowledge.variables === alert.id} />)}
              </tbody>
            </table>
          </div>}
    </div>
  </div>
}

function AlertRow({ alert, onAcknowledge, acknowledging }: {
  alert: Alert
  onAcknowledge: () => void
  acknowledging: boolean
}) {
  return <tr className="border-b border-slate-100 last:border-0 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/40">
    <td className="px-5 py-3 align-top"><SeverityPill severity={alert.severity} /></td>
    <td className="px-5 py-3 align-top">
      <p className="font-medium">{alert.summary}</p>
      <p className="mt-0.5 text-[13px] text-slate-500">
        {alert.checkName ?? alert.ruleId}
        {alert.lastValue !== null && ` · ${alert.lastValue}`}
        {alert.threshold !== null && ` (threshold ${alert.threshold})`}
      </p>
      <div className="mt-1 flex flex-wrap gap-2">
        {/* Both facts are only visible here: a flapping or muted rule publishes nothing at all. */}
        {alert.isFlapping && <span className="inline-flex items-center gap-1 rounded-md bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-500/15 dark:text-amber-300"><Waves size={12} />Flapping</span>}
        {alert.suppression !== 'None' && <span className="inline-flex rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-500/15 dark:text-slate-400">Suppressed: {alert.suppression}</span>}
      </div>
    </td>
    <td className="px-5 py-3 align-top">
      {alert.ciFound
        ? <>
          <Link to={`/assets/${alert.ciId}`} className="font-medium text-blue-600 hover:underline dark:text-blue-400">{alert.ciName}</Link>
          <p className="mt-0.5 text-[13px] text-slate-500">
            {alert.ownerName ?? 'Nobody holds this asset'}{alert.siteName && ` · ${alert.siteName}`}
          </p>
        </>
        // "Owner: —" on an unfindable CI reads as an unowned asset, which is a different fact (WP-3.7).
        : <span className="text-[13px] text-slate-500">Not found in the CMDB</span>}
      <p className="mt-0.5 text-[13px] text-slate-500">
        <Link to={`/monitoring/devices/${alert.deviceId}`} className="hover:underline">{alert.deviceAddress}</Link>
      </p>
    </td>
    <td className="px-5 py-3 align-top text-[13px] text-slate-500">
      <span title={formatLocal(alert.raisedAt)}>{formatAge(alert.raisedAt)}</span>
      {alert.clearedAt && <p className="mt-0.5">cleared {formatAge(alert.clearedAt)}</p>}
    </td>
    <td className="px-5 py-3 align-top text-[13px] text-slate-500">
      {alert.acknowledgedAt
        ? <span title={formatLocal(alert.acknowledgedAt)}>{alert.acknowledgedByName ?? alert.acknowledgedBy}</span>
        : <span className="text-slate-400">—</span>}
    </td>
    <td className="px-5 py-3 align-top text-right">
      {alert.status === 'Open' && !alert.acknowledgedAt && <Button variant="secondary" className="h-9 px-3"
        onClick={onAcknowledge} disabled={acknowledging}>
        <Check size={16} />{acknowledging ? 'Acknowledging' : 'Acknowledge'}
      </Button>}
    </td>
  </tr>
}

function CountTile({ label, value, icon: Icon, tone, loading, failed }: {
  label: string
  value: number | undefined
  icon: typeof BellRing
  tone: string
  loading: boolean
  failed: boolean
}) {
  return <div className="flex items-center gap-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <span className={cn('grid size-10 shrink-0 place-items-center rounded-full', tone)}><Icon size={20} /></span>
    <span className="min-w-0">
      <span className="block text-[13px] text-slate-500">{label}</span>
      {loading
        ? <span aria-label={`Counting ${label}`} className="mt-1 block h-8 w-16 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />
        : failed
          ? <span className="mt-1 block text-sm text-slate-400">Unavailable</span>
          : <span className="block text-[30px] font-bold leading-tight tabular-nums">{value ?? 0}</span>}
    </span>
  </div>
}

/** Whether a pushed alert still belongs under the filter the board is showing. */
export function matches(alert: Alert, filter: AlertFilter) {
  if (filter.status && alert.status !== filter.status) return false
  if (filter.severity && alert.severity !== filter.severity) return false
  if (filter.acknowledged === true && !alert.acknowledgedAt) return false
  if (filter.acknowledged === false && alert.acknowledgedAt) return false
  return true
}

/**
 * Worst first, then newest — the same order the server returns, so a patched-in alert lands where a
 * refetch would have put it rather than jumping when the page is next read.
 */
export function sortAlerts(items: Alert[]) {
  const rank = { Critical: 0, Warning: 1, Ok: 2 } as const
  return [...items].sort((left, right) =>
    rank[left.severity] - rank[right.severity]
    || new Date(right.raisedAt).getTime() - new Date(left.raisedAt).getTime()
    || left.id.localeCompare(right.id))
}

export { severityTone }
