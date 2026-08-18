import { ViewportPortal, type Node } from '@xyflow/react'
import { useMemo } from 'react'
import { nodeWidth } from './layout'
import type { TopologyNodeData } from './TopologyNodeCard'

/** How far a band extends past the nodes it encloses. */
const padding = 28
const labelHeight = 22

export type SiteBand = { siteName: string; x: number; y: number; width: number; height: number }

/**
 * The bounding box of each site's nodes.
 *
 * Computed from where the nodes actually are rather than from the layout, so a band follows a node
 * an operator has dragged. A site with a single node still gets one — the point is to say where the
 * boundary is, and a boundary around one machine is still true.
 */
export function siteBands(nodes: readonly Node<TopologyNodeData>[]): SiteBand[] {
  const extents = new Map<string, { left: number; top: number; right: number; bottom: number }>()

  for (const node of nodes) {
    if (node.hidden) continue
    const siteName = node.data.ci.siteName
    // §8: never inferred. A CI with no site recorded belongs to no band.
    if (siteName === null || siteName === '') continue

    const height = node.measured?.height ?? 76
    const width = node.measured?.width ?? nodeWidth
    const current = extents.get(siteName)
    const left = Math.min(current?.left ?? node.position.x, node.position.x)
    const top = Math.min(current?.top ?? node.position.y, node.position.y)
    const right = Math.max(current?.right ?? node.position.x + width, node.position.x + width)
    const bottom = Math.max(current?.bottom ?? node.position.y + height, node.position.y + height)
    extents.set(siteName, { left, top, right, bottom })
  }

  return [...extents.entries()]
    .map(([siteName, box]) => ({
      siteName,
      x: box.left - padding,
      y: box.top - padding - labelHeight,
      width: box.right - box.left + padding * 2,
      height: box.bottom - box.top + padding * 2 + labelHeight,
    }))
    .sort((a, b) => a.siteName.localeCompare(b.siteName, undefined, { sensitivity: 'base' }))
}

/**
 * Subtle labelled boundaries per site (§8), drawn behind the graph in viewport coordinates so they
 * pan and zoom with it.
 *
 * A hairline outline and a name, deliberately: §8 rules out heavy boxes and strong fills, and a band
 * that competes with the nodes inside it defeats the point. Nothing here is interactive — pointer
 * events pass straight through to the canvas beneath.
 */
export function SiteBands({ nodes, enabled }: {
  nodes: readonly Node<TopologyNodeData>[]
  enabled: boolean
}) {
  const bands = useMemo(() => enabled ? siteBands(nodes) : [], [nodes, enabled])
  if (bands.length === 0) return null

  return <ViewportPortal>
    {bands.map((band) => <div
      key={band.siteName}
      aria-hidden
      style={{
        position: 'absolute',
        transform: `translate(${band.x}px, ${band.y}px)`,
        width: band.width,
        height: band.height,
        pointerEvents: 'none',
        zIndex: -1,
      }}
      className="rounded-2xl border border-slate-200/80 bg-slate-50/40 dark:border-slate-700/60 dark:bg-slate-800/20">
      <span className="absolute left-3 top-1.5 text-[11px] font-medium uppercase tracking-wide text-slate-400 dark:text-slate-500">
        {band.siteName}
      </span>
    </div>)}
  </ViewportPortal>
}
