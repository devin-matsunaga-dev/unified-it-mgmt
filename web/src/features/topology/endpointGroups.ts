import type { TopologyEdge, TopologyNode, TopologyObservedLink } from '../../api/topology'

/**
 * Synthetic ids are prefixed so nothing can mistake one for a CI. This matters beyond rendering: the
 * saved-map payload is built from whatever is on the canvas, and posting a group id as a pinned CI
 * would send the server an id no CI has.
 */
export const endpointGroupPrefix = 'endpoint-group:'

export function endpointGroupId(parentCiId: string): string {
  return `${endpointGroupPrefix}${parentCiId}`
}

export function isEndpointGroupId(id: string): boolean {
  return id.startsWith(endpointGroupPrefix)
}

export type EndpointGroup = {
  id: string
  /** The network device the endpoints hang off. */
  parentCiId: string
  memberCiIds: string[]
}

/** Below this many, a group is more clutter than the endpoints it replaces. */
export const minimumGroupSize = 3

/**
 * The endpoints hanging off each network device.
 *
 * "Endpoint" is derived, never guessed from a name: a <c>Hardware</c> CI that nothing depends on and
 * that touches exactly one other CI, which is a network device. That is the safest definition the
 * data model supports — <c>CiType</c> has no Workstation or Laptop, so a desk machine and a rack
 * shelf are the same type, and the thing that actually separates them is how they are wired.
 *
 * A Hardware CI with two uplinks, or one something else depends on, is deliberately left alone: it is
 * doing something structural and hiding it would be hiding topology.
 */
export function findEndpointGroups(
  nodes: readonly TopologyNode[],
  edges: readonly TopologyEdge[],
  observedLinks: readonly TopologyObservedLink[] = [],
  minimumSize: number = minimumGroupSize,
): EndpointGroup[] {
  const byId = new Map(nodes.map((node) => [node.ciId, node]))
  const touching = new Map<string, Set<string>>()
  const dependedOnBy = new Map<string, number>()

  const join = (a: string, b: string) => {
    if (!byId.has(a) || !byId.has(b)) return
    for (const [from, to] of [[a, b], [b, a]] as const) {
      const existing = touching.get(from)
      if (existing) existing.add(to)
      else touching.set(from, new Set([to]))
    }
  }

  for (const edge of edges) {
    join(edge.sourceCiId, edge.targetCiId)
    // The source depends on the target, so the target is the one something depends on.
    if (byId.has(edge.targetCiId) && byId.has(edge.sourceCiId)) {
      dependedOnBy.set(edge.targetCiId, (dependedOnBy.get(edge.targetCiId) ?? 0) + 1)
    }
  }

  for (const link of observedLinks) join(link.sourceCiId, link.targetCiId)

  const membersByParent = new Map<string, string[]>()
  for (const node of nodes) {
    if (node.type !== 'Hardware') continue
    if ((dependedOnBy.get(node.ciId) ?? 0) > 0) continue

    const neighbours = touching.get(node.ciId)
    if (!neighbours || neighbours.size !== 1) continue

    const [parentCiId] = neighbours
    if (byId.get(parentCiId)?.type !== 'NetworkDevice') continue

    membersByParent.set(parentCiId, [...membersByParent.get(parentCiId) ?? [], node.ciId])
  }

  return [...membersByParent.entries()]
    .filter(([, members]) => members.length >= minimumSize)
    .map(([parentCiId, memberCiIds]) => ({
      id: endpointGroupId(parentCiId),
      parentCiId,
      memberCiIds: [...memberCiIds].sort(),
    }))
    .sort((a, b) => a.parentCiId.localeCompare(b.parentCiId))
}

export type DisplayGraph = {
  nodes: TopologyNode[]
  edges: TopologyEdge[]
  observedLinks: TopologyObservedLink[]
  groups: EndpointGroup[]
  /** Member CI id → the group it is folded into, for groups that are currently collapsed. */
  collapsedInto: Map<string, EndpointGroup>
}

/**
 * The graph as the canvas draws it: endpoints folded into one node per switch unless that group has
 * been expanded.
 *
 * A derived view, computed fresh from the real graph every time — nothing is mutated and no
 * relationship is dropped, so expanding a group restores exactly what the server sent. Everything
 * downstream (layout, adjacency, rendering) then works on this without knowing groups exist.
 */
export function collapseEndpoints(
  nodes: readonly TopologyNode[],
  edges: readonly TopologyEdge[],
  observedLinks: readonly TopologyObservedLink[],
  expanded: ReadonlySet<string>,
  minimumSize: number = minimumGroupSize,
): DisplayGraph {
  const groups = findEndpointGroups(nodes, edges, observedLinks, minimumSize)
  const collapsed = groups.filter((group) => !expanded.has(group.id))
  if (collapsed.length === 0) {
    return { nodes: [...nodes], edges: [...edges], observedLinks: [...observedLinks], groups, collapsedInto: new Map() }
  }

  const collapsedInto = new Map<string, EndpointGroup>()
  for (const group of collapsed) {
    for (const member of group.memberCiIds) collapsedInto.set(member, group)
  }

  const byId = new Map(nodes.map((node) => [node.ciId, node]))
  const groupNodes: TopologyNode[] = collapsed.map((group) => ({
    ciId: group.id,
    name: `${group.memberCiIds.length} endpoints`,
    // Hardware because that is what every member is; the card reads the group flag, not the type.
    type: 'Hardware',
    lifecycleState: 'Deployed',
    isActive: true,
    siteName: byId.get(group.parentCiId)?.siteName ?? null,
    address: null,
    lastSeenByDiscoveryAt: null,
    networkRole: null,
  }))

  /** One edge from the group to its switch, replacing however many the members had. */
  const groupEdges: TopologyEdge[] = collapsed.map((group) => ({
    id: `${group.id}:uplink`,
    sourceCiId: group.id,
    targetCiId: group.parentCiId,
    type: 'ConnectsTo',
    description: null,
    observedByDiscovery: false,
  }))

  const hidden = (ciId: string) => collapsedInto.has(ciId)

  return {
    nodes: [...nodes.filter((node) => !hidden(node.ciId)), ...groupNodes],
    edges: [...edges.filter((edge) => !hidden(edge.sourceCiId) && !hidden(edge.targetCiId)), ...groupEdges],
    observedLinks: observedLinks.filter((link) => !hidden(link.sourceCiId) && !hidden(link.targetCiId)),
    groups,
    collapsedInto,
  }
}
