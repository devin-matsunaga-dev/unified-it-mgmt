export type TileToneKey = string

/**
 * The colours a pinned tile's icon may take.
 *
 * Drawn from DESIGN.md §3's own families rather than a free colour picker: the semantic five, plus
 * the two categorical hues the charts use, plus neutral. A picker would let somebody choose a red
 * two shades off the critical red, which is worse than either — close enough to be mistaken for a
 * status, far enough to look like a mistake.
 *
 * The classes are written out rather than built from the key. Tailwind scans source text for class
 * names, so `bg-${tone}-100` is a class that never reaches the stylesheet.
 *
 * Only the icon's circle is tinted, exactly as the built-in tiles do it. DESIGN.md §12's rule that a
 * whole card is never repainted still holds — and the card border still belongs to whether the tile
 * is the filter currently applied.
 */
export const tileTones: readonly { key: TileToneKey; label: string; swatch: string; circle: string }[] = [
  {
    key: 'slate',
    label: 'Neutral',
    swatch: 'bg-slate-400',
    circle: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
  },
  {
    key: 'blue',
    label: 'Blue',
    swatch: 'bg-blue-600',
    circle: 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400',
  },
  {
    key: 'green',
    label: 'Green',
    swatch: 'bg-green-600',
    circle: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400',
  },
  {
    key: 'amber',
    label: 'Amber',
    swatch: 'bg-amber-500',
    circle: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  },
  {
    key: 'red',
    label: 'Red',
    swatch: 'bg-red-600',
    circle: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400',
  },
  {
    key: 'violet',
    label: 'Violet',
    swatch: 'bg-violet-500',
    circle: 'bg-violet-100 text-violet-700 dark:bg-violet-500/15 dark:text-violet-400',
  },
  {
    key: 'teal',
    label: 'Teal',
    swatch: 'bg-teal-500',
    circle: 'bg-teal-100 text-teal-700 dark:bg-teal-500/15 dark:text-teal-400',
  },
]

/** Neutral by default: a tile is a count, and a count is not a status. */
export const defaultTileTone: TileToneKey = 'slate'

/**
 * The circle classes for a tone, falling back rather than throwing — tiles outlive any one release,
 * and one naming a colour that has been retired must still draw.
 */
export function tileToneClasses(key: TileToneKey | undefined): string {
  return tileTones.find((tone) => tone.key === key)?.circle
    ?? tileTones.find((tone) => tone.key === defaultTileTone)!.circle
}
