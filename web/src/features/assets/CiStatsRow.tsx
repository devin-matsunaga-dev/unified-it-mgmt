import { useQueries } from '@tanstack/react-query'
import { Boxes, LayoutGrid, Pencil, Plus, Server, ShieldAlert, Wrench, X } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { assetsApi, type CiFilter } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import {
  allTiles, arrangeTiles, describeTileFilter, forgetTile, isTileApplied, maximumTiles, moveTile, narrowsAnything,
  readCustomTiles, readTileLayout, toggleTile, toTileFilter, visibleTiles, warrantyWindowDays,
  writeCustomTiles, writeTileLayout,
  type BuiltInTileId, type CustomTile, type Tile,
} from './ciTiles'
import { defaultTileIcon, tileIcon, tileIcons, type TileIconKey } from './ciTileIcons'
import { defaultTileTone, tileToneClasses, tileTones, type TileToneKey } from './ciTileTones'

const tilesKey = 'assets:tiles'
const tileLayoutKey = 'assets:tile-layout'

/**
 * Icon and tone per built-in. Tones follow the lifecycle pills on the table below rather than a
 * scale of their own — a Deployed tile in a different colour from every Deployed pill reads as a
 * different thing. The total is deliberately neutral: DESIGN.md §3 reserves colour for status, and a
 * count is not a status. A tile somebody pinned is neutral for the same reason: nothing knows whether
 * their question is good news.
 */
const builtInLooks: Record<BuiltInTileId, { icon: typeof Boxes; tone: string }> = {
  total: { icon: Boxes, tone: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400' },
  deployed: { icon: Server, tone: 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400' },
  repair: { icon: Wrench, tone: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400' },
  warranty: { icon: ShieldAlert, tone: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400' },
}

/**
 * The counts above the asset list — whichever ones the reader wants, in whatever order.
 *
 * Each is the list endpoint asked for a single row purely to read its `total`, which keeps the
 * numbers on the same definitions as the table, and means the existing `['cis']` invalidation
 * refreshes them after any edit with no second source of truth. That is also why the number of tiles
 * is capped: every one of them is a request.
 *
 * There is no "since last week" delta, unlike DESIGN.md §6's stat card: nothing in the CMDB records a
 * historical count, and a delta computed from nothing would be a decorative lie.
 */
export function CiStatsRow({ filter, onSelect }: { filter: CiFilter; onSelect: (next: CiFilter) => void }) {
  const [custom, setCustom] = useState<CustomTile[]>(() => readCustomTiles(tilesKey))
  const [layout, setLayout] = useState(() => readTileLayout(tileLayoutKey, readCustomTiles(tilesKey)))
  const [pinning, setPinning] = useState(false)
  const [editing, setEditing] = useState<Tile | null>(null)
  const [menu, setMenu] = useState(false)
  const [dragging, setDragging] = useState<string | null>(null)

  /**
   * Reconciled against the tiles that exist right now, rather than trusted as stored. A tile pinned
   * a moment ago is not in the order that was read at mount, and without this it would be saved,
   * counted against the cap, and invisible.
   */
  const arrangement = useMemo(() => arrangeTiles(custom, layout), [custom, layout])

  useEffect(() => writeCustomTiles(tilesKey, custom), [custom])
  useEffect(() => writeTileLayout(tileLayoutKey, arrangement), [arrangement])

  const known = useMemo(() => allTiles(custom), [custom])
  const shown = useMemo(() => visibleTiles(custom, arrangement), [custom, arrangement])

  const counts = useQueries({
    queries: shown.map((tile) => ({
      queryKey: ['cis', { ...tile.filter, pageSize: 1, page: 1 }],
      queryFn: () => assetsApi.listCis({ ...tile.filter, pageSize: 1, page: 1 }),
    })),
  })

  // Offered only when the current filter is something a tile could count, and is not already one.
  const canPin = narrowsAnything(filter)
    && !known.some((tile) => isTileApplied(filter, tile.filter))
    && shown.length < maximumTiles

  function removeTile(tile: Tile) {
    // A built-in is hidden so it can be brought back from the menu; one somebody pinned is theirs to
    // delete, and keeping a graveyard of removed tiles is not a feature.
    if (tile.custom) {
      setCustom((current) => current.filter((item) => item.id !== tile.id))
      setLayout(forgetTile(arrangement, tile.id))
      return
    }

    setLayout(toggleTile(arrangement, tile.id))
  }

  return <>
    <div className="flex items-center justify-between gap-3">
      <div className="relative">
        <Button variant="secondary" className="h-9" onClick={() => setMenu((value) => !value)} aria-expanded={menu}>
          <LayoutGrid size={16} />Tiles
        </Button>
        {menu && <div className="absolute left-0 top-11 z-20 w-64 rounded-lg border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-700 dark:bg-slate-800">
          {known.map((tile) => <label key={tile.id} className="flex h-9 items-center gap-2 rounded px-2 text-sm hover:bg-slate-50 dark:hover:bg-slate-700">
            <input type="checkbox" checked={!arrangement.hidden.includes(tile.id)}
              onChange={() => setLayout(toggleTile(arrangement, tile.id))} />
            <span className="min-w-0 flex-1 truncate">{tile.label}</span>
          </label>)}
          <p className="mt-1 border-t border-slate-200 px-2 pt-2 text-[12px] text-slate-500 dark:border-slate-700">
            Drag a tile to reorder it. Removing one you pinned deletes it.
          </p>
        </div>}
      </div>

      {canPin && <div className="flex min-w-0 items-center gap-2 text-[13px] text-slate-500">
        <span className="truncate">{describeTileFilter(filter)}</span>
        <Button variant="secondary" className="h-9 shrink-0" onClick={() => setPinning(true)}>
          <Plus size={16} />Pin as tile
        </Button>
      </div>}
    </div>

    {shown.length > 0 && <div role="group" aria-label="Estate counts" className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      {shown.map((tile, index) => {
        const count = counts[index]
        const applied = isTileApplied(filter, tile.filter)
        const look = tile.custom
          ? { icon: tileIcon(tile.icon), tone: tileToneClasses(tile.tone) }
          : builtInLooks[tile.id as BuiltInTileId]
        const { icon: Icon, tone } = look

        return <div key={tile.id}
          // Native drag, as the column headings and the dashboard's arrange mode use. The tile itself
          // is a <button>, which has no drag behaviour of its own, so the two do not fight.
          draggable
          onDragStart={(event) => {
            setDragging(tile.id)
            if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
          }}
          onDragOver={(event) => { if (dragging && dragging !== tile.id) event.preventDefault() }}
          onDrop={(event) => {
            event.preventDefault()
            // Based on the reconciled arrangement: a tile pinned moments ago is not yet in the
            // stored order, and moving it there would silently do nothing.
            if (dragging) setLayout(moveTile(arrangement, dragging, tile.id))
            setDragging(null)
          }}
          onDragEnd={() => setDragging(null)}
          className={cn('relative cursor-grab', dragging === tile.id && 'opacity-40')}>
          <button type="button" aria-pressed={applied}
            // Clicking an applied tile clears it, so a tile is never a filter the reader cannot undo.
            // The total counts everything, so there is nothing to clear it to.
            onClick={() => onSelect(applied && narrowsAnything(tile.filter) ? {} : tile.filter)}
            className={`flex w-full items-center gap-4 rounded-xl border bg-white p-5 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:bg-slate-900 ${applied ? 'border-blue-600 ring-1 ring-blue-600' : 'border-slate-200 hover:border-slate-300 dark:border-slate-800 dark:hover:border-slate-700'}`}>
            <span className={`grid size-10 shrink-0 place-items-center rounded-full ${tone}`}><Icon size={20} /></span>
            <span className="min-w-0">
              <span className="block truncate pr-12 text-[13px] text-slate-500">{tile.label}</span>
              {count.isLoading
                ? <span aria-label={`Counting ${tile.label}`} className="mt-1 block h-8 w-16 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />
                : count.isError
                  // A tile that cannot be counted says so; a zero here would be read as a fact.
                  ? <span className="mt-1 block text-sm text-slate-400">Unavailable</span>
                  : <span className="block text-[30px] font-bold leading-tight tabular-nums">{count.data?.total ?? 0}</span>}
            </span>
          </button>

          <span className="absolute right-2 top-2 flex gap-0.5">
            {tile.custom && <button type="button" aria-label={`Edit ${tile.label}`}
              className="rounded p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-slate-800 dark:hover:text-slate-200"
              onClick={() => setEditing(tile)}>
              <Pencil size={13} />
            </button>}
            <button type="button" aria-label={`Remove ${tile.label}`}
              className="rounded p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-slate-800 dark:hover:text-slate-200"
              onClick={() => removeTile(tile)}>
              <X size={13} />
            </button>
          </span>
        </div>
      })}
    </div>}

    {shown.length >= maximumTiles && <p className="text-[13px] text-slate-500">
      {maximumTiles} tiles is the limit — each one is a live count. Remove one to pin another.
    </p>}

    {pinning && <TileDialog
      title="Pin these filters as a tile"
      initialLabel={describeTileFilter(filter)}
      counts={describeTileFilter(filter)}
      onClose={() => setPinning(false)}
      onSave={(label, _recaptured, icon, tone) => {
        const id = `tile-${Date.now()}`
        // Date-based rather than a counter: two tiles pinned in different sessions must not collide.
        // Stripped to what a tile keeps: storing the raw filter would carry the search term and the
        // page into the tile, and its own count query would then quietly answer a different question.
        setCustom((current) => [...current, { id, label, icon, tone, filter: toTileFilter(filter) }])
        setPinning(false)
      }} />}

    {editing && <TileDialog
      title={`Edit ${editing.label}`}
      initialLabel={editing.label}
      initialIcon={editing.icon}
      initialTone={editing.tone}
      counts={describeTileFilter(editing.filter)}
      // Offered only when the list is showing something else, so the button always means something.
      recapture={narrowsAnything(filter) && !isTileApplied(filter, editing.filter)
        ? describeTileFilter(filter)
        : undefined}
      onClose={() => setEditing(null)}
      onSave={(label, recaptured, icon, tone) => {
        setCustom((current) => current.map((item) => item.id === editing.id
          ? { ...item, label, icon, tone, filter: recaptured ? toTileFilter(filter) : item.filter }
          : item))
        setEditing(null)
      }} />}
  </>
}

/**
 * Naming a tile, and re-pointing one that has been outgrown.
 *
 * The name is asked for rather than derived, because a tile somebody pinned is one they will read
 * every day — "Hardware · Deployed" describes the filter but not why they cared about it.
 */
function TileDialog({ title, initialLabel, initialIcon, initialTone, counts, recapture, onClose, onSave }: {
  title: string
  initialLabel: string
  initialIcon?: TileIconKey
  initialTone?: TileToneKey
  /** What the tile counts today, in words. */
  counts: string
  /** What the list is narrowed to now, when that differs and could replace the tile's filter. */
  recapture?: string
  onClose: () => void
  onSave: (label: string, recaptured: boolean, icon: TileIconKey, tone: TileToneKey) => void
}) {
  const [label, setLabel] = useState(initialLabel)
  const [icon, setIcon] = useState<TileIconKey>(initialIcon ?? defaultTileIcon)
  const [tone, setTone] = useState<TileToneKey>(initialTone ?? defaultTileTone)
  const [recaptured, setRecaptured] = useState(false)

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={title}>
    <form className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); if (label.trim()) onSave(label.trim(), recaptured, icon, tone) }}>
      <h2 className="text-lg font-semibold">{title}</h2>
      <p className="mt-1 text-sm text-slate-500">
        Counts {counts.toLowerCase()}, and clicking it brings the list back there.
      </p>

      <div className="mt-5">
        <label htmlFor="tile-label" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Name</label>
        <input id="tile-label" required maxLength={60} autoFocus className="input h-11"
          value={label} onChange={(event) => setLabel(event.target.value)} />
      </div>

      <fieldset className="mt-4">
        <legend className="text-[13px] font-medium text-slate-600 dark:text-slate-300">Icon</legend>
        <div role="radiogroup" aria-label="Icon" className="mt-2 flex flex-wrap gap-1">
          {tileIcons.map((option) => {
            const Icon = option.icon
            const chosen = option.key === icon
            return <button key={option.key} type="button"
              role="radio"
              aria-checked={chosen}
              aria-label={option.label}
              onClick={() => setIcon(option.key)}
              className={cn('grid size-9 place-items-center rounded-lg border transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
                chosen
                  ? `border-blue-600 ${tileToneClasses(tone)}`
                  : 'border-slate-200 text-slate-500 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800')}>
              <Icon size={17} />
            </button>
          })}
        </div>
      </fieldset>

      <fieldset className="mt-4">
        <legend className="text-[13px] font-medium text-slate-600 dark:text-slate-300">Colour</legend>
        <div role="radiogroup" aria-label="Colour" className="mt-2 flex flex-wrap gap-2">
          {tileTones.map((option) => <button key={option.key} type="button"
            role="radio"
            aria-checked={option.key === tone}
            aria-label={option.label}
            onClick={() => setTone(option.key)}
            className={cn('grid size-8 place-items-center rounded-full border-2 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
              option.key === tone ? 'border-slate-900 dark:border-slate-100' : 'border-transparent')}>
            <span aria-hidden className={cn('size-5 rounded-full', option.swatch)} />
          </button>)}
        </div>
      </fieldset>

      {recapture && <label className="mt-4 flex items-start gap-2 text-[13px] text-slate-600 dark:text-slate-300">
        <input type="checkbox" className="mt-0.5 size-4 rounded border-slate-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
          checked={recaptured} onChange={(event) => setRecaptured(event.target.checked)} />
        <span>Point it at what the list shows now instead — {recapture.toLowerCase()}</span>
      </label>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={label.trim() === ''}>Save tile</Button>
      </div>
    </form>
  </div>
}
