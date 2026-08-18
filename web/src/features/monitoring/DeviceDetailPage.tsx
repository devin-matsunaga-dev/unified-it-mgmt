import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Check } from 'lucide-react'
import { Suspense, lazy, useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { monitoringApi, type Alert, type DeviceInterface, type DeviceStatusTile } from '../../api/monitoring'
import { cn } from '../../lib/utils'
import { DeviceInterfaceTable } from './DeviceInterfaceTable'
import { LiveIndicator } from './LiveIndicator'
import { metricRanges, parseSeriesKey, seriesKey, windowFor, type RangeKey } from './metricRanges'
import { SeverityPill, formatAge, formatLocal, statusDot, statusLabel, statusTone } from './severity'
import { useMonitoringHub } from './useMonitoringHub'
import { usePageHeading } from '../../layout/pageHeading'

/**
 * Recharts is a third of the bundle and this is the only screen that draws a chart, so it is split
 * out: every other page in the app — including both boards — loads without it.
 */
const MetricChart = lazy(() => import('./MetricChart').then((module) => ({ default: module.MetricChart })))

/**
 * One device: its current status, its open alerts, and a chart of any metric it reports. The chart's
 * picker is discovered from the data (WP-3.4) rather than declared, and lists one entry per metric
 * *and check*, because that is what a series is.
 */
export function DeviceDetailPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const [range, setRange] = useState<RangeKey>('24h')
  const [selected, setSelected] = useState<string | null>(null)
  const [selectedInterface, setSelectedInterface] = useState<number | null>(null)

  const device = useQuery({ queryKey: ['monitoring', 'device', id], queryFn: () => monitoringApi.getDevice(id) })
  usePageHeading(device.data ? { title: device.data.ciName ?? device.data.address } : null)
  const tile = useQuery({ queryKey: ['monitoring', 'device-tile', id], queryFn: () => monitoringApi.statusBoard({ pageSize: 200 }).then((board) => board.items.find((item) => item.deviceId === id) ?? null) })
  const checks = useQuery({ queryKey: ['monitoring', 'checks', id], queryFn: () => monitoringApi.listChecks(id) })
  const metrics = useQuery({ queryKey: ['monitoring', 'metrics', id], queryFn: () => monitoringApi.listMetrics(id) })
  const interfaces = useQuery({ queryKey: ['monitoring', 'interfaces', id], queryFn: () => monitoringApi.listInterfaces(id) })
  const alerts = useQuery({
    queryKey: ['monitoring', 'alerts', { deviceId: id, status: 'Open' }],
    queryFn: () => monitoringApi.listAlerts({ deviceId: id, status: 'Open', pageSize: 200 }),
  })

  // The first metric the device reports, so the chart is never empty on arrival while the reader
  // works out that there is a picker.
  useEffect(() => {
    if (!selected && metrics.data?.length) {
      const first = metrics.data[0]
      setSelected(seriesKey(first.metric, first.checkId))
    }
  }, [metrics.data, selected])

  const chosen = selected ? parseSeriesKey(selected) : null

  // Charting a port is charting one of its series, so selecting a row does what the picker does
  // rather than opening a second kind of chart. Inbound traffic is the series it lands on: it is the
  // one an operator means by "what is that port doing", and the picker is right there for the rest.
  const onSelectInterface = useCallback((link: DeviceInterface) => {
    setSelectedInterface(link.ifIndex)
    setSelected(seriesKey(`${link.metricPrefix}bits_in_per_second`, link.checkId))
  }, [])
  const window = useMemo(() => windowFor(range), [range])
  const series = useQuery({
    queryKey: ['monitoring', 'series', id, chosen, window],
    queryFn: () => monitoringApi.getSeries(id, {
      metric: chosen!.metric,
      checkId: chosen!.checkId,
      from: window.from,
      to: window.to,
      resolution: window.resolution,
    }),
    enabled: Boolean(chosen),
  })

  const onAlertChanged = useCallback((alert: Alert) => {
    if (alert.deviceId !== id) return
    void queryClient.invalidateQueries({ queryKey: ['monitoring', 'alerts'] })
  }, [id, queryClient])

  const onDeviceStatusChanged = useCallback((next: DeviceStatusTile) => {
    if (next.deviceId !== id) return
    queryClient.setQueryData(['monitoring', 'device-tile', id], next)
  }, [id, queryClient])

  const onResync = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['monitoring'] })
  }, [queryClient])

  const events = useMemo(
    () => ({ onAlertChanged, onDeviceStatusChanged, onResync }),
    [onAlertChanged, onDeviceStatusChanged, onResync])
  const hub = useMonitoringHub(events)

  // The check behind the chosen series, for its thresholds — the lines the chart draws are the ones
  // the alert engine actually judges against, read from the check rather than typed in twice.
  const check = checks.data?.find((entry) => entry.id === chosen?.checkId)

  // An interface check's thresholds are about one port's utilisation, so they are lines on that
  // series and on no other: 90% drawn across a chart of bits per second is a line at 90 bit/s, which
  // sits on the axis and means nothing.
  const thresholdsApply = !chosen?.metric.startsWith('interface.')
    || chosen.metric.endsWith('.utilisation_percent')
  const status = tile.data?.status ?? 'Unknown'

  if (device.isError) {
    return <p className="text-sm text-slate-500">This device could not be read.</p>
  }

  return <div className="space-y-6">
    <div className="flex flex-wrap items-center gap-3">
      <Link to="/monitoring" className="inline-flex items-center gap-1.5 text-sm text-slate-500 hover:text-slate-700 dark:hover:text-slate-300">
        <ArrowLeft size={16} />Status board
      </Link>
      <div className="ml-auto"><LiveIndicator status={hub} /></div>
    </div>

    <div className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap items-start gap-4">
        <span className={cn('mt-2 size-3 shrink-0 rounded-full', statusDot[status])} aria-hidden />
        <div className="min-w-0 flex-1">
          <h2 className="text-xl font-bold">{device.data?.ciName ?? device.data?.address ?? 'Device'}</h2>
          <p className="mt-1 text-sm text-slate-500">
            {device.data?.address}
            {device.data?.siteName && ` · ${device.data.siteName}`}
            {device.data?.pollerGroup && ` · poller group ${device.data.pollerGroup}`}
          </p>
        </div>
        <span className={cn('rounded-md px-2 py-0.5 text-xs font-medium', statusTone[status])}>{statusLabel[status]}</span>
      </div>

      <dl className="mt-5 grid gap-4 border-t border-slate-100 pt-4 text-sm sm:grid-cols-3 dark:border-slate-800">
        <div><dt className="text-[13px] text-slate-500">Configuration item</dt>
          <dd className="mt-0.5">{device.data?.ciId
            ? <Link to={`/assets/${device.data.ciId}`} className="text-blue-600 hover:underline dark:text-blue-400">{device.data.ciName ?? 'Open asset'}</Link>
            : '—'}</dd></div>
        <div><dt className="text-[13px] text-slate-500">Checks</dt>
          <dd className="mt-0.5 tabular-nums">{checks.data?.length ?? device.data?.checkCount ?? 0}</dd></div>
        <div><dt className="text-[13px] text-slate-500">Last reading</dt>
          <dd className="mt-0.5">{tile.data?.lastTelemetryAt
            ? <span title={formatLocal(tile.data.lastTelemetryAt)}>{formatAge(tile.data.lastTelemetryAt)}</span>
            : <span className="text-slate-400">No readings yet</span>}</dd></div>
      </dl>
    </div>

    <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <h3 className="text-base font-semibold">Metrics</h3>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <label className="sr-only" htmlFor="metric-picker">Metric</label>
          <select id="metric-picker" value={selected ?? ''} onChange={(event) => setSelected(event.target.value)}
            className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm dark:border-slate-700 dark:bg-slate-900">
            {(metrics.data ?? []).map((metric) => <option key={seriesKey(metric.metric, metric.checkId)} value={seriesKey(metric.metric, metric.checkId)}>
              {metric.metric}{metric.checkName ? ` — ${metric.checkName}` : ''}
            </option>)}
          </select>
          <label className="sr-only" htmlFor="range-picker">Range</label>
          <select id="range-picker" value={range} onChange={(event) => setRange(event.target.value as RangeKey)}
            className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm dark:border-slate-700 dark:bg-slate-900">
            {metricRanges.map((entry) => <option key={entry.key} value={entry.key}>{entry.label}</option>)}
          </select>
        </div>
      </div>

      {metrics.isLoading
        ? <div aria-label="Loading metrics" className="h-64 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />
        : (metrics.data ?? []).length === 0
          ? <p className="py-10 text-center text-sm text-slate-500">This device has not reported any metrics in the last two days.</p>
          : series.isLoading
            ? <div aria-label="Loading chart" className="h-64 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />
            : series.isError
              ? <p className="py-10 text-center text-sm text-slate-500">{(series.error as Error).message}</p>
              : series.data
                ? <Suspense fallback={<div aria-label="Loading chart" className="h-64 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />}>
                  <MetricChart series={series.data} range={range}
                    warning={thresholdsApply ? check?.warningThreshold : null}
                    critical={thresholdsApply ? check?.criticalThreshold : null} />
                </Suspense>
                : null}
    </section>

    {/* Shown for a device that reports interfaces, and for one that could — an SNMP device with no
        interface rows gets the empty state telling somebody how to poll them, while a mail server
        with an HTTP check does not get a card about ports it will never have. */}
    {(interfaces.isLoading || (interfaces.data ?? []).length > 0
      || (checks.data ?? []).some((check) => check.type === 'Snmp')) && <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <h3 className="text-base font-semibold">Interfaces</h3>
        {(interfaces.data ?? []).length > 0 && <span className="text-[13px] text-slate-500">
          {interfaces.data!.filter((link) => link.operStatus === 'Up').length} of {interfaces.data!.length} up
        </span>}
      </div>
      <DeviceInterfaceTable
        interfaces={interfaces.data ?? []}
        isLoading={interfaces.isLoading}
        selectedIfIndex={selectedInterface}
        onSelect={onSelectInterface} />
    </section>}

    <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h3 className="mb-4 text-base font-semibold">Open alerts</h3>
      {(alerts.data?.items ?? []).length === 0
        ? <p className="py-6 text-center text-sm text-slate-500">Nothing is alerting on this device.</p>
        : <ul className="space-y-3">
          {alerts.data!.items.map((alert) => <li key={alert.id} className="flex flex-wrap items-start gap-3 border-b border-slate-100 pb-3 last:border-0 last:pb-0 dark:border-slate-800">
            <SeverityPill severity={alert.severity} />
            <div className="min-w-0 flex-1">
              <p className="text-sm">{alert.summary}</p>
              <p className="mt-0.5 text-[13px] text-slate-500">
                {alert.checkName ?? alert.ruleId} · raised <span title={formatLocal(alert.raisedAt)}>{formatAge(alert.raisedAt)}</span>
              </p>
            </div>
            {alert.acknowledgedAt && <span className="inline-flex items-center gap-1 text-[13px] text-slate-500">
              <Check size={14} />{alert.acknowledgedByName ?? alert.acknowledgedBy}
            </span>}
          </li>)}
        </ul>}
      <Link to="/monitoring/alerts" className="mt-4 inline-block text-[13px] text-blue-600 hover:underline dark:text-blue-400">
        Open the alert board
      </Link>
    </section>
  </div>
}
