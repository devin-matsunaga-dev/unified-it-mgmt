import { Link } from 'react-router-dom'
import { ciTypeLabel, type Ci } from '../../api/assets'
import { ciLifecycleLabel, ciLifecycleTone } from './lifecycle'
import type { CiSortColumn } from './ciSort'

/**
 * One column of the asset table, defined once and used for both the header and the cell.
 *
 * They used to be separate: the header was generated from `ciSortLabels` while the cells were
 * written out in a fixed order. That worked only because neither could move. The moment a column can
 * be hidden or dragged, two lists that have to agree and nothing making them agree is a table that
 * silently prints "Type" over asset tags.
 */
export type CiColumn = {
  /** Also the sort key, because every data column here is sortable. */
  id: CiSortColumn
  label: string
  cell: (ci: Ci) => React.ReactNode
  /** Extra classes for the cell — alignment and the muted mono treatment for identifiers. */
  className?: string
}

const muted = 'text-slate-600 dark:text-slate-300'
const identifier = 'font-mono text-xs text-slate-500'

/**
 * The defined order, which is also the order a table nobody has arranged appears in. Selection and
 * the row actions are deliberately not here: neither is a column anybody would hide or move, and
 * both would need row callbacks that have nothing to do with a CI's data.
 */
export const ciColumns: readonly CiColumn[] = [
  {
    id: 'name',
    label: 'Name',
    cell: (ci) => <Link to={`/assets/${ci.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{ci.name}</Link>,
  },
  { id: 'type', label: 'Type', cell: (ci) => ciTypeLabel(ci.type), className: muted },
  { id: 'assetTag', label: 'Asset tag', cell: (ci) => ci.assetTag ?? '—', className: identifier },
  { id: 'serialNumber', label: 'Serial', cell: (ci) => ci.serialNumber ?? '—', className: identifier },
  {
    id: 'lifecycleState',
    label: 'Lifecycle',
    cell: (ci) => <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(ci.lifecycleState)}`}>
      {ciLifecycleLabel(ci.lifecycleState)}
    </span>,
  },
  { id: 'owner', label: 'Owner', cell: (ci) => ci.ownership.ownerName ?? '—', className: muted },
  { id: 'department', label: 'Department', cell: (ci) => ci.ownership.departmentName ?? '—', className: muted },
  { id: 'site', label: 'Location', cell: (ci) => ci.ownership.siteName ?? '—', className: muted },
  {
    id: 'isActive',
    label: 'State',
    cell: (ci) => <StatePill isActive={ci.isActive} />,
  },
]

export const ciColumnIds = ciColumns.map((column) => column.id)

export function ciColumn(id: CiSortColumn): CiColumn {
  const found = ciColumns.find((column) => column.id === id)
  if (!found) throw new Error(`Unknown asset column '${id}'.`)
  return found
}

/** Moved here with the table cell it belongs to; the page had no other use for it. */
function StatePill({ isActive }: { isActive: boolean }) {
  return isActive
    ? <span className="rounded-md bg-green-100 px-2 py-0.5 text-xs font-medium text-green-700 dark:bg-green-500/15 dark:text-green-400">Active</span>
    : <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-500/15 dark:text-slate-400">Inactive</span>
}
