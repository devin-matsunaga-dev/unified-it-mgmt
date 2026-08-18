/**
 * A table's arrangement: which columns are shown, and in what order.
 *
 * Kept as pure data with the known column ids passed in, so a column removed from the app cannot be
 * resurrected by somebody's saved layout, and a column added later appears for everybody instead of
 * being invisible to anyone who has ever arranged the table.
 */
export type ColumnLayout<Id extends string> = {
  /** Every known id, in the order to draw them. */
  order: Id[]
  /** Ids explicitly hidden. Absent means shown, so a new column is visible by default. */
  hidden: Id[]
}

export function defaultLayout<Id extends string>(known: readonly Id[]): ColumnLayout<Id> {
  return { order: [...known], hidden: [] }
}

/**
 * Reconciles a stored layout against the columns that actually exist now.
 *
 * Unknown ids are dropped and missing ones appended in their defined order, so neither a rename nor a
 * new column leaves somebody with a broken table they cannot fix without clearing site data.
 */
export function reconcileLayout<Id extends string>(
  known: readonly Id[],
  stored: Partial<ColumnLayout<Id>> | null,
): ColumnLayout<Id> {
  if (!stored) return defaultLayout(known)

  const valid = new Set(known)
  const kept = (stored.order ?? []).filter((id) => valid.has(id))
  const seen = new Set(kept)
  const order = [...kept, ...known.filter((id) => !seen.has(id))]
  const hidden = (stored.hidden ?? []).filter((id) => valid.has(id))
  return { order, hidden }
}

export function visibleColumns<Id extends string>(layout: ColumnLayout<Id>): Id[] {
  const hidden = new Set(layout.hidden)
  return layout.order.filter((id) => !hidden.has(id))
}

export function isColumnVisible<Id extends string>(layout: ColumnLayout<Id>, id: Id): boolean {
  return !layout.hidden.includes(id)
}

/**
 * Hiding the last visible column is refused rather than allowed: an empty table is not a view of
 * anything, and the way back would be a menu the operator can no longer see a table beside.
 */
export function toggleColumn<Id extends string>(layout: ColumnLayout<Id>, id: Id): ColumnLayout<Id> {
  if (!layout.hidden.includes(id)) {
    return visibleColumns(layout).length <= 1
      ? layout
      : { ...layout, hidden: [...layout.hidden, id] }
  }

  return { ...layout, hidden: layout.hidden.filter((item) => item !== id) }
}

/**
 * Moves `id` to where `target` currently sits, shifting the rest along — the behaviour a drag onto
 * another header implies. Moving a column onto itself, or either id being unknown, is a no-op.
 */
export function moveColumn<Id extends string>(
  layout: ColumnLayout<Id>,
  id: Id,
  target: Id,
): ColumnLayout<Id> {
  if (id === target) return layout
  const from = layout.order.indexOf(id)
  const to = layout.order.indexOf(target)
  if (from < 0 || to < 0) return layout

  const order = [...layout.order]
  order.splice(from, 1)
  order.splice(to, 0, id)
  return { ...layout, order }
}

/**
 * Reads a stored layout. Anything unreadable falls back to the default rather than throwing — a
 * corrupt preference must not take the page down with it.
 */
export function readLayout<Id extends string>(key: string, known: readonly Id[]): ColumnLayout<Id> {
  try {
    const raw = localStorage.getItem(key)
    return reconcileLayout(known, raw ? JSON.parse(raw) as Partial<ColumnLayout<Id>> : null)
  } catch {
    return defaultLayout(known)
  }
}

export function writeLayout<Id extends string>(key: string, layout: ColumnLayout<Id>): void {
  try {
    localStorage.setItem(key, JSON.stringify(layout))
  } catch {
    // A full or blocked store is not worth a broken page: the table still works, it just forgets.
  }
}
