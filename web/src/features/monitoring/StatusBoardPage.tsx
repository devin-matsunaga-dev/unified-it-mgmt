import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, CircleAlert, CircleHelp, PowerOff, Search, ShieldCheck, TriangleAlert } from 'lucide-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { monitoringApi, type DeviceStatus, type DeviceStatusTile } from '../../api/monitoring'
import { cn } from '../../lib/utils'
import { LiveIndicator } from './LiveIndicator'
import { MonitoringTabs } from './MonitoringTabs'
import { formatAge, statusDot, statusLabel, statusTone } from './severity'
import { useMonitoringHub } from './useMonitoringHub'

/** The board asks for a full page of tiles; the footer says so when the estate is bigger. */
const boardPageSize = 200

const tiles: { key: DeviceStatus | 'devices'; label: string; icon: typeof Activity; tone: string }[] = [
  { key: 'devices', label: 'Monitored devices', icon: Activity, tone: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400' },
  { key: 'Critical', label: 'Critical', icon: CircleAlert, tone: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400' },
  { key: 'Warning', label: 'Warning', icon: TriangleAlert, tone: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400' },
  { key: 'Ok', label: 'Healthy', icon: ShieldCheck, tone: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400' },
]

/**
 * A wall of device tiles, coloured by the worst thing currently wrong with each. Read over HTTP on
 * load and kept in step by the hub afterwards: a push replaces the one tile it names rather than
 * refetching the board, so a device changing state does not make every other tile flicker.
 */
export function StatusBoardPage() {
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [applied, setApplied] = useState('')
  const [statusFilter, setStatusFilter] = useState<DeviceStatus | null>(null)

  useEffect(() => {
    const timer = window.setTimeout(() => setApplied(search), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  const queryKey = useMemo(() => ['monitoring', 'status-board', { search: applied }], [applied])
  const board = useQuery({
    queryKey,
    queryFn: () => monitoringApi.statusBoard({ search: applied || undefined, pageSize: boardPageSize }),
    placeholderData: keepPreviousData,
  })

  const onDeviceStatusChanged = useCallback((tile: DeviceStatusTile) => {
    // Replaced in place. The counts row is left alone until the next read: recomputing it in the
    // browser from one tile would be guessing at a number the server counts over the whole estate.
    queryClient.setQueriesData<Awaited<ReturnType<typeof monitoringApi.statusBoard>>>(
      { queryKey: ['monitoring', 'status-board'] },
      (current) => current && ({
        ...current,
        items: current.items.map((item) => item.deviceId === tile.deviceId ? tile : item),
      }),
    )
  }, [queryClient])

  const onResync = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['monitoring'] })
  }, [queryClient])

  const events = useMemo(() => ({ onDeviceStatusChanged, onResync }), [onDeviceStatusChanged, onResync])
  const hub = useMonitoringHub(events)

  const counts = board.data?.counts
  const shown = (board.data?.items ?? []).filter((tile) => !statusFilter || tile.status === statusFilter)
  const total = board.data?.total ?? 0

  return <div className="space-y-6">
    <MonitoringTabs right={<LiveIndicator status={hub} />} />

    <div role="group" aria-label="Estate health" className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      {tiles.map((tile) => {
        const Icon = tile.icon
        const value = !counts ? null : tile.key === 'devices' ? counts.devices
          : tile.key === 'Critical' ? counts.critical
            : tile.key === 'Warning' ? counts.warning : counts.ok
        const isFilter = tile.key !== 'devices'
        const isApplied = isFilter && statusFilter === tile.key

        return <button key={tile.key} type="button" aria-pressed={isFilter ? isApplied : undefined}
          disabled={!isFilter}
          onClick={() => isFilter && setStatusFilter(isApplied ? null : tile.key as DeviceStatus)}
          className={cn('flex items-center gap-4 rounded-xl border bg-white p-5 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:bg-slate-900',
            isApplied ? 'border-blue-600 ring-1 ring-blue-600' : 'border-slate-200 dark:border-slate-800',
            isFilter && !isApplied && 'hover:border-slate-300 dark:hover:border-slate-700',
            !isFilter && 'disabled:opacity-100')}>
          <span className={cn('grid size-10 shrink-0 place-items-center rounded-full', tile.tone)}><Icon size={20} /></span>
          <span className="min-w-0">
            <span className="block text-[13px] text-slate-500">{tile.label}</span>
            {board.isLoading
              ? <span aria-label={`Counting ${tile.label}`} className="mt-1 block h-8 w-16 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />
              : board.isError
                // A count that could not be read is not zero — WP-2.11's rule, same reason.
                ? <span className="mt-1 block text-sm text-slate-400">Unavailable</span>
                : <span className="block text-[30px] font-bold leading-tight tabular-nums">{value}</span>}
          </span>
        </button>
      })}
    </div>

    {counts && (counts.unknown > 0 || counts.disabled > 0) && <p className="text-sm text-slate-500">
      {counts.unknown > 0 && <span className="mr-4 inline-flex items-center gap-1.5"><CircleHelp size={15} />{counts.unknown} not yet reported</span>}
      {counts.disabled > 0 && <span className="inline-flex items-center gap-1.5"><PowerOff size={15} />{counts.disabled} disabled</span>}
    </p>}

    <div className="flex h-10 max-w-sm items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
      <Search size={18} />
      <input aria-label="Search devices by address" value={search} onChange={(event) => setSearch(event.target.value)}
        placeholder="Search by address..." className="w-full bg-transparent text-sm outline-none" />
    </div>

    {board.isLoading
      ? <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
        {[0, 1, 2, 3, 4, 5].map((key) => <div key={key} aria-label="Loading devices" className="h-32 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />)}
      </div>
      : shown.length === 0
        ? <div className="rounded-xl border border-slate-200 bg-white p-10 text-center dark:border-slate-800 dark:bg-slate-900">
          <span className="mx-auto mb-3 grid size-12 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800"><Activity size={22} /></span>
          <p className="text-sm text-slate-500">
            {total === 0
              ? 'No devices are monitored yet. Add one to see it here.'
              : 'No device matches this filter.'}
          </p>
        </div>
        : <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {shown.map((tile) => <DeviceTile key={tile.deviceId} tile={tile} />)}
        </div>}

    {total > boardPageSize && <p className="text-sm text-slate-500">
      Showing the first {boardPageSize} of {total} devices — narrow the search to see the rest.
    </p>}
  </div>
}

function DeviceTile({ tile }: { tile: DeviceStatusTile }) {
  return <Link to={`/monitoring/devices/${tile.deviceId}`}
    className="block rounded-xl border border-slate-200 bg-white p-5 transition-colors hover:border-slate-300 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-800 dark:bg-slate-900 dark:hover:border-slate-700">
    <div className="flex items-start gap-3">
      <span className={cn('mt-1.5 size-2 shrink-0 rounded-full', statusDot[tile.status])} aria-hidden />
      <div className="min-w-0 flex-1">
        <p className="truncate font-semibold">{tile.ciName ?? tile.address}</p>
        <p className="truncate text-[13px] text-slate-500">
          {tile.address}{tile.siteName && ` · ${tile.siteName}`}
        </p>
      </div>
      <span className={cn('shrink-0 rounded-md px-2 py-0.5 text-xs font-medium', statusTone[tile.status])}>
        {statusLabel[tile.status]}
      </span>
    </div>

    {tile.headline && <p className="mt-3 line-clamp-2 text-sm text-slate-600 dark:text-slate-300">{tile.headline}</p>}

    <div className="mt-4 flex flex-wrap items-center gap-x-4 gap-y-1 text-[13px] text-slate-500">
      <span className="tabular-nums">{tile.checkCount} check{tile.checkCount === 1 ? '' : 's'}</span>
      {tile.openAlerts > 0 && <span className="tabular-nums">{tile.openAlerts} open alert{tile.openAlerts === 1 ? '' : 's'}</span>}
      {/* An acknowledgement never changes the colour — it says somebody is on it, not that it is better. */}
      {tile.acknowledgedAlerts > 0 && <span className="tabular-nums">{tile.acknowledgedAlerts} acknowledged</span>}
      {tile.worstAlertRaisedAt
        ? <span>since {formatAge(tile.worstAlertRaisedAt)}</span>
        : tile.lastTelemetryAt
          ? <span>last reading {formatAge(tile.lastTelemetryAt)}</span>
          : <span>no readings yet</span>}
    </div>
  </Link>
}
