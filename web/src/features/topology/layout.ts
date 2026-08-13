import type { TopologyEdge, TopologyMapNode, TopologyNode, TopologyObservedLink } from '../../api/topology'

export type Position = { x: number; y: number }

/**
 * Node box and the gaps around it. The width is the widest a node card gets before its name
 * truncates; the layout has to agree with the card or the columns overlap.
 */
export const nodeWidth = 216
export const nodeHeight = 76
const gapX = 40
const gapY = 104

/**
 * Where every node sits when nobody has arranged them.
 *
 * A dependency graph has a natural up: an edge means the source needs the target, so the target
 * belongs above it. Layering by longest path from the things that depend on nothing puts the routers
 * on the top row, the switches under them, the hosts under those and the VMs under those — which is
 * how anybody sketching this estate on a whiteboard would draw it, and it falls out of the edge
 * direction rather than out of CI type. A layout keyed on type would put a virtualised firewall in
 * the wrong place and have no answer at all for `Logical`.
 *
 * Observed links are deliberately not used for layering. They are undirected — a cable has no "up" —
 * and a scan seeing two core switches patched together would otherwise stack one under the other and
 * say something about dependency that nobody asserted. They still count for horizontal ordering,
 * where the only claim being made is "these two belong near each other".
 */
export function autoLayout(
  nodes: TopologyNode[],
  edges: TopologyEdge[],
  observedLinks: TopologyObservedLink[] = [],
): Map<string, Position> {
  const known = new Set(nodes.map((node) => node.ciId))
  const dependsOn = new Map<string, string[]>()
  for (const edge of edges) {
    if (!known.has(edge.sourceCiId) || !known.has(edge.targetCiId)) continue
    dependsOn.set(edge.sourceCiId, [...dependsOn.get(edge.sourceCiId) ?? [], edge.targetCiId])
  }

  const layerOf = new Map<string, number>()
  const onPath = new Set<string>()
  const layer = (ciId: string): number => {
    const settled = layerOf.get(ciId)
    if (settled !== undefined) return settled

    // A cycle is legal here — WP-2.3 accepts mutually dependent CIs deliberately — so the walk
    // refuses to re-enter a node already on its own path and treats that edge as contributing
    // nothing, exactly as the server's traversal does. Without this the layout would not terminate.
    if (onPath.has(ciId)) return 0

    onPath.add(ciId)
    let depth = 0
    for (const target of dependsOn.get(ciId) ?? []) {
      depth = Math.max(depth, layer(target) + 1)
    }
    onPath.delete(ciId)
    layerOf.set(ciId, depth)
    return depth
  }

  for (const node of nodes) layer(node.ciId)

  const byLayer = new Map<number, TopologyNode[]>()
  for (const node of nodes) {
    const index = layerOf.get(node.ciId) ?? 0
    byLayer.set(index, [...byLayer.get(index) ?? [], node])
  }

  // Neighbours in every direction, for the ordering sweep below.
  const neighbours = new Map<string, string[]>()
  const join = (a: string, b: string) => {
    if (!known.has(a) || !known.has(b)) return
    neighbours.set(a, [...neighbours.get(a) ?? [], b])
    neighbours.set(b, [...neighbours.get(b) ?? [], a])
  }
  for (const edge of edges) join(edge.sourceCiId, edge.targetCiId)
  for (const link of observedLinks) join(link.sourceCiId, link.targetCiId)

  const positions = new Map<string, Position>()
  const columnOf = new Map<string, number>()
  const layers = [...byLayer.keys()].sort((a, b) => a - b)

  for (const index of layers) {
    const row = byLayer.get(index) ?? []
    // One barycentre sweep against the layer already placed above: a node sits over the average
    // column of the neighbours it is joined to. Not a crossing-minimising algorithm — that is a
    // research problem and this is a picture an operator drags around anyway — but it is the
    // difference between "every VM under its own host" and "alphabetical spaghetti".
    const ordered = [...row].sort((a, b) => {
      const weight = (node: TopologyNode) => {
        const placed = (neighbours.get(node.ciId) ?? [])
          .map((other) => columnOf.get(other))
          .filter((column): column is number => column !== undefined)
        return placed.length === 0
          ? Number.MAX_SAFE_INTEGER
          : placed.reduce((total, column) => total + column, 0) / placed.length
      }

      const difference = weight(a) - weight(b)
      return difference !== 0 ? difference : a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
    })

    const width = ordered.length * nodeWidth + Math.max(0, ordered.length - 1) * gapX
    ordered.forEach((node, column) => {
      columnOf.set(node.ciId, column)
      positions.set(node.ciId, {
        x: column * (nodeWidth + gapX) - width / 2,
        y: index * (nodeHeight + gapY),
      })
    })
  }

  return positions
}

/**
 * The layout the canvas actually renders: auto-layout everywhere, overridden by whatever the saved
 * map has pinned.
 *
 * A pin for a CI that is not on the map is ignored rather than dropped from the saved map — the type
 * filter hides CIs without un-pinning them, and a save while a filter is applied must not throw away
 * the positions of everything the operator cannot currently see.
 */
export function resolveLayout(
  nodes: TopologyNode[],
  edges: TopologyEdge[],
  observedLinks: TopologyObservedLink[],
  pins: TopologyMapNode[],
): Map<string, Position> {
  const positions = autoLayout(nodes, edges, observedLinks)
  const known = new Set(nodes.map((node) => node.ciId))
  for (const pin of pins) {
    if (known.has(pin.ciId)) positions.set(pin.ciId, { x: pin.x, y: pin.y })
  }

  return positions
}

/**
 * What a save sends: every position now on the canvas, plus the pins for CIs the current filter
 * hides. Without the second half, saving a network-only view would un-pin every server on the map.
 */
export function mergePins(
  onCanvas: TopologyMapNode[],
  existing: TopologyMapNode[],
  visible: Set<string>,
): TopologyMapNode[] {
  return [...existing.filter((pin) => !visible.has(pin.ciId)), ...onCanvas]
}
