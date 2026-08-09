import { useQueries } from '@tanstack/react-query'
import { Boxes, ShieldAlert, Server, Wrench } from 'lucide-react'
import { assetsApi, type CiFilter } from '../../api/assets'

/** The window the WP-2.6 renewal job treats as "expiring soon", so the tile and the notices agree. */
const warrantyWindowDays = 30

/**
 * One tile per question the estate is usually opened with. Each carries the filter that answers it,
 * so a number the reader wants to interrogate is one click from the list showing exactly those rows.
 *
 * Tones follow the lifecycle pills on the table below rather than a scale of their own — a Deployed
 * tile in a different colour from every Deployed pill reads as a different thing. The total is
 * deliberately neutral: DESIGN.md §3 reserves colour for status, and a count is not a status.
 */
const tiles: { key: string; label: string; icon: typeof Boxes; tone: string; filter: CiFilter }[] = [
  { key: 'total', label: 'Configuration items', icon: Boxes, tone: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400', filter: {} },
  { key: 'deployed', label: 'Deployed', icon: Server, tone: 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400', filter: { lifecycleState: 'Deployed' } },
  { key: 'repair', label: 'In repair', icon: Wrench, tone: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400', filter: { lifecycleState: 'InRepair' } },
  { key: 'warranty', label: `Warranty ends within ${warrantyWindowDays} days`, icon: ShieldAlert, tone: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400', filter: { warrantyExpiringWithinDays: warrantyWindowDays } },
]

/** Whether the list is currently narrowed to exactly what a tile counts. */
function isApplied(filter: CiFilter, tile: CiFilter) {
  const total = !tile.lifecycleState && tile.warrantyExpiringWithinDays === undefined
  return total
    ? !filter.lifecycleState && filter.warrantyExpiringWithinDays === undefined
    : filter.lifecycleState === tile.lifecycleState && filter.warrantyExpiringWithinDays === tile.warrantyExpiringWithinDays
}

/**
 * The four counts above the asset list. Each is the list endpoint asked for a single row purely to
 * read its `total`, which keeps the numbers on the same definitions as the table — and means the
 * existing `['cis']` invalidation refreshes them after any edit, with no second source of truth.
 *
 * There is no "since last week" delta on these tiles, unlike DESIGN.md §6's stat card: nothing in the
 * CMDB records a historical count, and a delta computed from nothing would be a decorative lie.
 */
export function CiStatsRow({ filter, onSelect }: { filter: CiFilter; onSelect: (next: CiFilter) => void }) {
  const counts = useQueries({
    queries: tiles.map((tile) => ({
      queryKey: ['cis', { ...tile.filter, pageSize: 1, page: 1 }],
      queryFn: () => assetsApi.listCis({ ...tile.filter, pageSize: 1, page: 1 }),
    })),
  })

  return <div role="group" aria-label="Estate counts" className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
    {tiles.map((tile, index) => {
      const count = counts[index]
      const applied = isApplied(filter, tile.filter)
      const Icon = tile.icon

      return <button key={tile.key} type="button" aria-pressed={applied}
        // Clicking an applied tile clears it, so a tile is never a filter the reader cannot undo.
        onClick={() => onSelect(applied && tile.key !== 'total' ? {} : tile.filter)}
        className={`flex items-center gap-4 rounded-xl border bg-white p-5 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:bg-slate-900 ${applied ? 'border-blue-600 ring-1 ring-blue-600' : 'border-slate-200 hover:border-slate-300 dark:border-slate-800 dark:hover:border-slate-700'}`}>
        <span className={`grid size-10 shrink-0 place-items-center rounded-full ${tile.tone}`}><Icon size={20} /></span>
        <span className="min-w-0">
          <span className="block text-[13px] text-slate-500">{tile.label}</span>
          {count.isLoading
            ? <span aria-label={`Counting ${tile.label}`} className="mt-1 block h-8 w-16 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />
            : count.isError
              // A tile that cannot be counted says so; a zero here would be read as a fact.
              ? <span className="mt-1 block text-sm text-slate-400">Unavailable</span>
              : <span className="block text-[30px] font-bold leading-tight tabular-nums">{count.data?.total ?? 0}</span>}
        </span>
      </button>
    })}
  </div>
}
