import { Handle, Position, type NodeProps } from '@xyflow/react'
import { AppWindow, Boxes, HardDrive, MonitorSmartphone, Network, Pin, Server } from 'lucide-react'
import { Link } from 'react-router-dom'
import type { CiType } from '../../api/assets'
import type { DeviceStatus } from '../../api/monitoring'
import type { TopologyNode } from '../../api/topology'
import { cn } from '../../lib/utils'
import { statusDot, statusLabel } from '../monitoring/severity'
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
}

/**
 * DESIGN.md §9: a white bordered mini-card with an icon, a name and a status dot. Live status recolours
 * the dot and the border and nothing else — a wall of red cards is a wall nobody can read, and the
 * name has to stay legible at exactly the moment the device is on fire.
 */
export function TopologyNodeCard({ data, selected }: NodeProps & { data: TopologyNodeData }) {
  const { ci, status, deviceId, pinned } = data
  const Icon = typeIcons[ci.type] ?? Boxes
  const to = deviceId ? `/monitoring/devices/${deviceId}` : `/assets/${ci.ciId}`

  return <div
    style={{ width: nodeWidth, height: nodeHeight }}
    className={cn('group relative flex items-center gap-3 rounded-lg border bg-white px-3 py-2 dark:bg-slate-900',
      status === 'Critical' ? 'border-red-500 dark:border-red-500'
        : status === 'Warning' ? 'border-amber-500 dark:border-amber-500'
          : status === 'Ok' ? 'border-green-500 dark:border-green-500'
            : 'border-slate-200 dark:border-slate-800',
      selected && 'ring-2 ring-blue-600')}>
    <Handle type="target" position={Position.Top} className="!size-1.5 !border-0 !bg-slate-300" />

    <span className="grid size-9 shrink-0 place-items-center rounded-full bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">
      <Icon size={18} />
    </span>

    <span className="min-w-0 flex-1">
      <Link to={to} className="block truncate text-[13px] font-medium text-slate-900 hover:text-blue-600 hover:underline dark:text-slate-100">
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
        </span>
      </span>
    </span>

    {pinned && <Pin size={12} aria-label="Pinned" className="absolute right-1.5 top-1.5 text-blue-600" />}

    <Handle type="source" position={Position.Bottom} className="!size-1.5 !border-0 !bg-slate-300" />
  </div>
}
