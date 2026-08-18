import { ciTypeLabel, type Ci } from '../../api/assets'
import { ciLifecycleLabel, ciLifecycleStates } from './lifecycle'

export type CiSortColumn = 'name' | 'type' | 'assetTag' | 'serialNumber' | 'lifecycleState' | 'owner' | 'department' | 'site' | 'isActive'

export type CiSort = { column: CiSortColumn; desc: boolean }

/**
 * What each column sorts on. Most read as the cell does, so the order matches what the eye compares.
 *
 * Lifecycle is the exception: it sorts by position in the WP-2.2 state graph (Ordered → Disposed)
 * rather than alphabetically, because "Deployed, Disposed, In repair, In stock" is an ordering of
 * spellings, not of asset lives, and nobody reads a lifecycle column looking for the letter D.
 *
 * Owner used to fall back to the department when nobody held the asset. That fallback existed only
 * because department had no column of its own; now that it has one, the fallback would print the same
 * name in two adjacent cells, so owner sorts and reads as the owner alone.
 */
const sortKeys: Record<CiSortColumn, (ci: Ci) => string | number | null> = {
  name: (ci) => ci.name,
  type: (ci) => ciTypeLabel(ci.type),
  assetTag: (ci) => ci.assetTag,
  serialNumber: (ci) => ci.serialNumber,
  lifecycleState: (ci) => ciLifecycleStates.indexOf(ci.lifecycleState),
  owner: (ci) => ci.ownership.ownerName,
  department: (ci) => ci.ownership.departmentName,
  site: (ci) => ci.ownership.siteName,
  isActive: (ci) => (ci.isActive ? 0 : 1),
}

/** The header label each column sorts under, so the table and the sort agree on one name per column. */
export const ciSortLabels: Record<CiSortColumn, string> = {
  name: 'Name',
  type: 'Type',
  assetTag: 'Asset tag',
  serialNumber: 'Serial',
  lifecycleState: 'Lifecycle',
  owner: 'Owner',
  department: 'Department',
  site: 'Location',
  isActive: 'State',
}

/**
 * Clicking a header cycles ascending → descending → unsorted, matching the ticket list. Returning to
 * unsorted matters here because the server's own order (name, then id) is the only stable one across
 * pages, so an operator has to be able to get back to it.
 */
export function nextCiSort(current: CiSort | null, column: CiSortColumn): CiSort | null {
  if (current?.column !== column) return { column, desc: false }
  return current.desc ? null : { column, desc: true }
}

/**
 * The rows in sort order. Blank cells always sort last in both directions — a CI with no serial is
 * missing a value rather than holding the smallest one, and burying it at the top of a descending
 * sort hides the rows the operator asked to see.
 */
export function sortCis(items: Ci[], sort: CiSort | null): Ci[] {
  if (!sort) return items
  const key = sortKeys[sort.column]
  const direction = sort.desc ? -1 : 1

  return [...items].sort((left, right) => {
    const a = key(left)
    const b = key(right)
    if (a === null || a === '') return b === null || b === '' ? 0 : 1
    if (b === null || b === '') return -1
    const compared = typeof a === 'number' && typeof b === 'number' ? a - b : String(a).localeCompare(String(b))
    // Name is the server's tiebreak too, so an equal pair keeps the order the list arrived in.
    return compared === 0 ? left.name.localeCompare(right.name) : compared * direction
  })
}

/** A sorted column stated for a screen reader, since the icon alone carries the direction visually. */
export function ciSortDescription(sort: CiSort | null, column: CiSortColumn): 'ascending' | 'descending' | 'none' {
  if (sort?.column !== column) return 'none'
  return sort.desc ? 'descending' : 'ascending'
}

/** Lifecycle values in graph order, for a test to assert against without importing the graph twice. */
export const ciLifecycleOrder = ciLifecycleStates.map(ciLifecycleLabel)
