import type { CiFilter } from '../../api/assets'
import { moveColumn, reconcileLayout, type ColumnLayout } from './columnLayout'

/**
 * A tile is a saved filter with a name and a live count. That is what the built-in four already
 * were — each asks the list endpoint for a single row purely to read its `total` — so letting people
 * add their own is a matter of storing filters, not of inventing a widget.
 */
export type CustomTile = {
  id: string
  label: string
  filter: CiFilter
  /** A key from the tile icon catalogue. Absent on tiles pinned before icons could be chosen. */
  icon?: string
  /** A key from the tile tone palette. Absent means neutral. */
  tone?: string
}

export type BuiltInTileId = 'total' | 'deployed' | 'repair' | 'warranty'

/** The window the WP-2.6 renewal job treats as "expiring soon", so the tile and the notices agree. */
export const warrantyWindowDays = 30

export const builtInTiles: readonly { id: BuiltInTileId; label: string; filter: CiFilter }[] = [
  { id: 'total', label: 'Configuration items', filter: {} },
  { id: 'deployed', label: 'Deployed', filter: { lifecycleState: 'Deployed' } },
  { id: 'repair', label: 'In repair', filter: { lifecycleState: 'InRepair' } },
  { id: 'warranty', label: `Warranty ends within ${warrantyWindowDays} days`, filter: { warrantyExpiringWithinDays: warrantyWindowDays } },
]

export const builtInTileIds = builtInTiles.map((tile) => tile.id)

/**
 * Every tile is one request. Four is what the page has always cost; letting somebody add their own
 * makes that number theirs to choose, so it is capped. Beyond this a counts endpoint taking several
 * filters at once is the right answer, not a bigger number here.
 */
export const maximumTiles = 8

/**
 * What a tile remembers of a filter.
 *
 * Search is deliberately excluded: it is a lookup somebody is doing right now, not a slice of the
 * estate they want to keep. Paging is excluded because a tile is a question, not a position in the
 * answer.
 */
const tileKeys = [
  'type', 'customFields', 'isActive', 'lifecycleState', 'ownerUserId',
  'departmentId', 'siteId', 'contractId', 'warrantyExpiringWithinDays',
] as const

/** Strips a filter to what a tile keeps, dropping empty members so two equal filters compare equal. */
export function toTileFilter(filter: CiFilter): CiFilter {
  const kept: CiFilter = {}
  for (const key of tileKeys) {
    const value = filter[key]
    if (value === undefined) continue
    if (Array.isArray(value) && value.length === 0) continue
    Object.assign(kept, { [key]: value })
  }

  return kept
}

/** Whether a filter narrows anything at all. A tile counting the whole estate is the built-in total. */
export function narrowsAnything(filter: CiFilter): boolean {
  return Object.keys(toTileFilter(filter)).length > 0
}

/**
 * Whether the list is currently narrowed to exactly what a tile counts.
 *
 * Compared on the whole tile filter rather than on named members, which is what the previous version
 * did — it knew about lifecycle and warranty only, and special-cased the total. A tile carrying a
 * type or an owner would have read as applied while the list showed something else.
 */
export function isTileApplied(filter: CiFilter, tile: CiFilter): boolean {
  const current = toTileFilter(filter)
  const wanted = toTileFilter(tile)
  const keys = Object.keys(wanted)
  if (Object.keys(current).length !== keys.length) return false
  return keys.every((key) => JSON.stringify(current[key as keyof CiFilter]) === JSON.stringify(wanted[key as keyof CiFilter]))
}

/**
 * A short description of what a tile counts, for the label somebody has not named themselves and for
 * the confirmation before one is saved.
 */
export function describeTileFilter(filter: CiFilter): string {
  const parts: string[] = []
  const tile = toTileFilter(filter)
  if (tile.type) parts.push(tile.type)
  if (tile.lifecycleState) parts.push(tile.lifecycleState)
  if (tile.isActive !== undefined) parts.push(tile.isActive ? 'active' : 'inactive')
  if (tile.ownerUserId) parts.push('one owner')
  if (tile.departmentId) parts.push('one department')
  if (tile.siteId) parts.push('one location')
  if (tile.contractId) parts.push('one contract')
  if (tile.warrantyExpiringWithinDays !== undefined) parts.push(`warranty within ${tile.warrantyExpiringWithinDays} days`)
  for (const constraint of tile.customFields ?? []) parts.push(constraint.value)
  return parts.length > 0 ? parts.join(' · ') : 'Everything'
}

export function readCustomTiles(key: string): CustomTile[] {
  try {
    const raw = localStorage.getItem(key)
    if (!raw) return []
    const parsed = JSON.parse(raw) as unknown
    if (!Array.isArray(parsed)) return []
    // Each entry is checked rather than trusted: this is user-editable storage, and one bad row must
    // not cost the whole set.
    return parsed.filter((tile): tile is CustomTile =>
      typeof tile === 'object' && tile !== null
      && typeof (tile as CustomTile).id === 'string'
      && typeof (tile as CustomTile).label === 'string'
      && typeof (tile as CustomTile).filter === 'object' && (tile as CustomTile).filter !== null
      // The icon is optional and validated at render, where an unknown key falls back to a pin: a
      // tile stored by an older version has none, and one naming a retired icon must still draw.
      && ((tile as CustomTile).icon === undefined || typeof (tile as CustomTile).icon === 'string')
      && ((tile as CustomTile).tone === undefined || typeof (tile as CustomTile).tone === 'string'))
  } catch {
    return []
  }
}

export function writeCustomTiles(key: string, tiles: CustomTile[]): void {
  try {
    localStorage.setItem(key, JSON.stringify(tiles))
  } catch {
    // A full or blocked store forgets the tile; it must not take the page down.
  }
}

/** One tile as the row draws it, whether it came with the app or somebody pinned it. */
export type Tile = {
  id: string
  label: string
  filter: CiFilter
  custom: boolean
  icon?: string
  tone?: string
}

/**
 * Order and removal, over built-in and custom tiles alike.
 *
 * The same shape the column menu uses, and reused rather than re-derived. Built-ins are *hidden*
 * rather than deleted so they can be brought back; a custom tile is deleted outright, because the
 * person who made it can make it again and a graveyard of things they removed is not a feature.
 */
export type TileLayout = ColumnLayout<string>

export function allTiles(custom: readonly CustomTile[]): Tile[] {
  return [
    ...builtInTiles.map((tile) => ({ id: tile.id, label: tile.label, filter: tile.filter, custom: false })),
    ...custom.map((tile) => ({
      id: tile.id, label: tile.label, filter: tile.filter, custom: true, icon: tile.icon, tone: tile.tone,
    })),
  ]
}

/** Reconciles a stored arrangement against the tiles that exist now, customs included. */
export function arrangeTiles(custom: readonly CustomTile[], stored: Partial<TileLayout> | null): TileLayout {
  return reconcileLayout(allTiles(custom).map((tile) => tile.id), stored)
}

/** The tiles to draw, in order. */
export function visibleTiles(custom: readonly CustomTile[], layout: TileLayout): Tile[] {
  const byId = new Map(allTiles(custom).map((tile) => [tile.id, tile]))
  const hidden = new Set(layout.hidden)
  return layout.order
    .filter((id) => !hidden.has(id))
    .map((id) => byId.get(id))
    .filter((tile): tile is Tile => tile !== undefined)
}

/**
 * Hiding and showing a tile. Unlike a column, the last one may go: the row simply disappears, and the
 * button that pins a new one lives outside it, so there is always a way back.
 */
export function toggleTile(layout: TileLayout, id: string): TileLayout {
  return layout.hidden.includes(id)
    ? { ...layout, hidden: layout.hidden.filter((item) => item !== id) }
    : { ...layout, hidden: [...layout.hidden, id] }
}

export function moveTile(layout: TileLayout, id: string, target: string): TileLayout {
  return moveColumn(layout, id, target)
}

/** Forgetting a deleted custom tile's arrangement, so a later tile cannot inherit its place. */
export function forgetTile(layout: TileLayout, id: string): TileLayout {
  return {
    order: layout.order.filter((item) => item !== id),
    hidden: layout.hidden.filter((item) => item !== id),
  }
}

export function readTileLayout(key: string, custom: readonly CustomTile[]): TileLayout {
  try {
    const raw = localStorage.getItem(key)
    return arrangeTiles(custom, raw ? JSON.parse(raw) as Partial<TileLayout> : null)
  } catch {
    return arrangeTiles(custom, null)
  }
}

export function writeTileLayout(key: string, layout: TileLayout): void {
  try {
    localStorage.setItem(key, JSON.stringify(layout))
  } catch {
    // A full or blocked store forgets the arrangement; it must not take the page down.
  }
}
