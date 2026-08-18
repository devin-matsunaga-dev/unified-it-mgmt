import { Handle, Position, type NodeProps } from '@xyflow/react'
import { memo } from 'react'
import { AppWindow, Boxes, HardDrive, MonitorSmartphone, Network, Pin, Server, Users } from 'lucide-react'
import { Link } from 'react-router-dom'
import type { CiType } from '../../api/assets'
import type { DeviceStatus } from '../../api/monitoring'
import type { TopologyNode } from '../../api/topology'
import { cn } from '../../lib/utils'
import { statusDot, statusLabel } from '../monitoring/severity'
import type { EndpointGroup } from './endpointGroups'
import type { Emphasis } from './focus'
import { tierFor, tierStyles, type CorrelationRole } from './nodeEmphasis'
import { nodeHeight, nodeWidth } from './layout'

/** Same icons the CI page's relations graph uses, so a node is recognisable on either screen. */
const typeIcons: Record<CiType, typeof Server> = {
  Hardware: MonitorSmartphone,
  Server: Server,
  NetworkDevice: Network,
  Software: AppWindow,
  Virtual: HardDrive,
  Logical: Boxes,
}

export type TopologyNodeData = {
  ci: TopologyNode
  /** Null when nothing monitors this CI — which is not a health claim and must not look like one. */
  status: DeviceStatus | null
  deviceId: string | null
  pinned: boolean
  /**
   * How prominent this node is for the current selection. 'related' is the resting state — with
   * nothing selected every node is 'related', so the map is never permanently faded.
   */
  emphasis: Emphasis
  /** Set when this node stands for folded endpoints rather than a CI; null for a real CI. */
  group: EndpointGroup | null
  /** Open alerts on this CI, for the count on the card. Zero when there are none. */
  openAlerts: number
  /** Its part in a correlated failure (§13), or null when it is not in one. */
  correlation: CorrelationRole | null
}

/**
 * DESIGN.md §9: a white bordered mini-card with an icon, a name and a status dot. Live status recolours
 * the dot and the border and nothing else — a wall of red cards is a wall nobody can read, and the
 * name has to stay legible at exactly the moment the device is on fire.
 */
function TopologyNodeCardInner({ data, selected }: NodeProps & { data: TopologyNodeData }) {
  const { ci, status, deviceId, pinned, emphasis, group, openAlerts, correlation } = data
  const tier = tierStyles[tierFor(ci)]
  const Icon = group ? Users : typeIcons[ci.type] ?? Boxes
  const to = deviceId ? `/monitoring/devices/${deviceId}` : `/assets/${ci.ciId}`

  // A group is a drawing, not a CI: it has nothing to open and no health of its own, so it renders
  // its own card rather than pretending to be a node with a link and a status dot.
  if (group) {
    return <div
      style={{ width: nodeWidth, height: nodeHeight }}
      className={cn('flex items-center gap-3 rounded-lg border border-dashed border-slate-300 bg-slate-50 px-3 py-2 dark:border-slate-700 dark:bg-slate-800/50',
        emphasis === 'muted' && 'opacity-25',
        emphasis === 'selected' && 'ring-2 ring-blue-600 ring-offset-1')}>
      <Handle type="source" position={Position.Top} className="!size-1.5 !border-0 !bg-slate-300" />
      <span className="grid size-9 shrink-0 place-items-center rounded-full bg-slate-200 text-slate-600 dark:bg-slate-700 dark:text-slate-300">
        <Icon size={18} />
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[13px] font-medium text-slate-900 dark:text-slate-100">{ci.name}</span>
        <span className="mt-0.5 block truncate text-[11px] text-slate-500">Click to expand</span>
      </span>
      <Handle type="target" position={Position.Bottom} className="!size-1.5 !border-0 !bg-slate-300" />
    </div>
  }

  return <div
    style={{ width: nodeWidth, height: tier.height }}
    className={cn('group relative flex items-center gap-3 rounded-lg bg-white px-3 py-2 dark:bg-slate-900',
      tier.border,
      status === 'Critical' ? 'border-red-500 dark:border-red-500'
        : status === 'Warning' ? 'border-amber-500 dark:border-amber-500'
          : status === 'Ok' ? 'border-green-500 dark:border-green-500'
            : 'border-slate-200 dark:border-slate-800',
      // §13: the cause is outlined, what it took out is only dimmed at the edge. Restrained on
      // purpose — the whole node is never repainted, which DESIGN.md §12 forbids.
      correlation === 'RootCause' && 'ring-2 ring-red-500/70',
      correlation === 'Affected' && 'border-dashed',
      // Fading is opacity only: a muted node keeps its size and position, so the shape of the estate
      // is still readable while one dependency path is being followed. DESIGN.md §5's calm rule.
      emphasis === 'muted' && 'opacity-25',
      emphasis === 'selected' && 'ring-2 ring-blue-600 ring-offset-1',
      selected && 'ring-2 ring-blue-600')}>
    {/*
      * Source on top, target on bottom, because this graph draws dependencies UPWARD: an edge runs
      * from the dependent (lower) to the thing it needs (higher). With the handles the other way the
      * line has to leave the bottom of a node and loop back up to reach the top of one above it,
      * which is what made every switch sprout a curl.
      */}
    <Handle type="source" position={Position.Top} className="!size-1.5 !border-0 !bg-slate-300" />

    <span className="grid size-9 shrink-0 place-items-center rounded-full bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">
      <Icon size={tier.icon} />
    </span>

    <span className="min-w-0 flex-1">
      <Link to={to} className={cn('block truncate text-slate-900 hover:text-blue-600 hover:underline dark:text-slate-100', tier.name)}>
        {ci.name}
      </Link>
      <span className="mt-0.5 flex items-center gap-1.5">
        <span
          aria-hidden
          className={cn('size-2 shrink-0 rounded-full', status ? statusDot[status] : 'bg-slate-300 dark:bg-slate-700')} />
        <span className="truncate text-[11px] text-slate-500">
          {/* "Not monitored" rather than a health word: nothing has ever asked this CI how it is. */}
          {status ? statusLabel[status] : 'Not monitored'}
          {ci.address ? ` · ${ci.address}` : ''}
          {openAlerts > 0 ? ` · ${openAlerts} ${openAlerts === 1 ? 'alert' : 'alerts'}` : ''}
        </span>
      </span>
    </span>

    {correlation && <span
      className={cn('absolute right-1.5 top-1.5 rounded px-1 text-[10px] font-medium',
        correlation === 'RootCause'
          ? 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400'
          : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400')}>
      {correlation === 'RootCause' ? 'Root cause' : 'Affected'}
    </span>}

    {pinned && !correlation && <Pin size={12} aria-label="Pinned" className="absolute right-1.5 top-1.5 text-blue-600" />}

    <Handle type="target" position={Position.Bottom} className="!size-1.5 !border-0 !bg-slate-300" />
  </div>
}

/**
 * Memoised, and it matters at this size: React Flow hands every node the same `nodes` array
 * identity, so without this a status push, a selection or a drag re-renders every card on the map
 * rather than the ones whose data actually moved.
 *
 * The effects that write status, emphasis and correlation all return the *same* node object when
 * nothing changed, which is what makes this shallow comparison effective rather than merely present.
 */
export const TopologyNodeCard = memo(TopologyNodeCardInner)
