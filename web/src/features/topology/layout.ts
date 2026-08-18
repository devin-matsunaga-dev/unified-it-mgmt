import type { TopologyEdge, TopologyMapNode, TopologyNode, TopologyObservedLink } from '../../api/topology'

/**
 * Where a recorded network role sits in the hierarchy, mirroring the server's NetworkDeviceRoles.
 * A device without a role contributes nothing — its layer comes from its dependencies alone.
 */
const roleLayer: Record<string, number> = {
  Edge: 0,
  Firewall: 1,
  Core: 2,
  Distribution: 3,
  Access: 4,
  Wireless: 5,
}

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

  /**
   * A recorded role is a floor, not a replacement (§9). Dependencies can push a device further down
   * — a switch behind two routers belongs below both — but nothing can pull a Core switch above an
   * Edge router just because the relationship between them was never written down. Devices with no
   * role are unaffected, so this changes nothing for a CMDB that has not filled the field in.
   */
  const floorOf = new Map<string, number>()
  for (const node of nodes) {
    const floor = node.networkRole === null ? undefined : roleLayer[node.networkRole]
    if (floor !== undefined) floorOf.set(node.ciId, floor)
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
    let depth = floorOf.get(ciId) ?? 0
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

  const columnOf = new Map<string, number>()
  const layers = [...byLayer.keys()].sort((a, b) => a - b)

  // Seed each layer alphabetically so the first downward sweep has something stable to weigh
  // against, and so a graph with no edges at all still comes out in a predictable order.
  const order = new Map<number, TopologyNode[]>()
  for (const index of layers) {
    const row = [...byLayer.get(index) ?? []]
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
    order.set(index, row)
    row.forEach((node, column) => columnOf.set(node.ciId, column))
  }

  /**
   * One barycentre pass over a layer: a node moves to the average column of the neighbours it is
   * joined to, keeping its current column when it has none placed. Sorting is stable and ties fall
   * back to the existing order, so a pass never churns nodes it has no opinion about.
   */
  const sweep = (index: number) => {
    const row = order.get(index) ?? []
    const weight = new Map<string, number>()
    row.forEach((node, column) => {
      const placed = (neighbours.get(node.ciId) ?? [])
        .map((other) => columnOf.get(other))
        .filter((column): column is number => column !== undefined)
      weight.set(node.ciId, placed.length === 0
        ? column
        : placed.reduce((total, value) => total + value, 0) / placed.length)
    })

    const resorted = [...row].sort((a, b) => (weight.get(a.ciId) ?? 0) - (weight.get(b.ciId) ?? 0))
    order.set(index, resorted)
    resorted.forEach((node, column) => columnOf.set(node.ciId, column))
  }

  /**
   * Sweeps alternate down the layers and back up, four times.
   *
   * A single downward pass only ever weighs a node against the layer above it, so the widest layers
   * — the leaves, which is most of this estate — are ordered by whatever their parents happened to
   * do and nothing pulls a parent back over its children. Alternating is the standard Sugiyama
   * ordering heuristic and it is what stops long edges raking across the whole picture. Four is
   * empirical: the arrangement stops moving well before it, and this runs on every graph change.
   */
  for (let iteration = 0; iteration < 4; iteration++) {
    for (const index of layers) sweep(index)
    for (const index of [...layers].reverse()) sweep(index)
  }

  const positions = new Map<string, Position>()
  for (const index of layers) {
    const ordered = order.get(index) ?? []
    const width = ordered.length * nodeWidth + Math.max(0, ordered.length - 1) * gapX
    ordered.forEach((node, column) => {
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
