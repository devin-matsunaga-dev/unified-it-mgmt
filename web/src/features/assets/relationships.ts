import type { CiGraph, CiGraphNode, CiRelationship, CiRelationshipType, CiType } from '../../api/assets'

export const ciRelationshipTypes: CiRelationshipType[] = ['RunsOn', 'ConnectsTo', 'DependsOn', 'HostedOn']

/**
 * Each type as the verb that makes an edge a sentence. WP-2.3 fixed source → target to read "source
 * needs target", so these only ever run in that direction.
 */
const relationshipVerbs: Record<CiRelationshipType, string> = {
  RunsOn: 'runs on',
  ConnectsTo: 'connects to',
  DependsOn: 'depends on',
  HostedOn: 'is hosted on',
}

export function ciRelationshipVerb(type: string) {
  return relationshipVerbs[type as CiRelationshipType] ?? type
}

/** Which way an edge points from the CI whose page is open: upstream is what it needs. */
export type RelationshipDirection = 'Upstream' | 'Downstream'

/** The far end of an edge, so a row can name the other CI without the reader working out which end it is. */
export type RelationshipCounterpart = {
  direction: RelationshipDirection
  ciId: string
  name: string
  type: CiType
}

export function relationshipCounterpart(edge: CiRelationship, ciId: string): RelationshipCounterpart {
  return edge.sourceCiId === ciId
    ? { direction: 'Upstream', ciId: edge.targetCiId, name: edge.targetCiName, type: edge.targetCiType }
    : { direction: 'Downstream', ciId: edge.sourceCiId, name: edge.sourceCiName, type: edge.sourceCiType }
}

/** One edge as plain English, always read source-first so the arrow and the words agree. */
export function describeRelationship(edge: CiRelationship): string {
  return `${edge.sourceCiName} ${ciRelationshipVerb(edge.type)} ${edge.targetCiName}`
}

/**
 * The heading for a group of direct edges that share a type and a direction. Upstream reads as the
 * bare verb because the open CI is the subject ("Runs on" — this CI runs on these); downstream keeps
 * "this CI" as the object, because there the members of the group are the subject.
 */
export function relationshipGroupLabel(type: string, direction: RelationshipDirection): string {
  const verb = ciRelationshipVerb(type)
  const heading = direction === 'Upstream' ? verb : `${verb} this CI`
  return heading.charAt(0).toUpperCase() + heading.slice(1)
}

/** One heading's worth of direct edges, each already resolved to the CI at its far end. */
export type RelationshipGroup = {
  key: string
  label: string
  direction: RelationshipDirection
  edges: { edge: CiRelationship; counterpart: RelationshipCounterpart }[]
}

/**
 * The CI's direct edges gathered under one heading per (direction, type), upstream first, so the card
 * answers "what does this run on" in one place rather than interleaving both directions by creation order.
 */
export function groupDirectRelationships(edges: CiRelationship[], ciId: string): RelationshipGroup[] {
  const groups = new Map<string, RelationshipGroup>()
  for (const edge of edges) {
    const counterpart = relationshipCounterpart(edge, ciId)
    const key = `${counterpart.direction}:${edge.type}`
    const group = groups.get(key)
      ?? { key, label: relationshipGroupLabel(edge.type, counterpart.direction), direction: counterpart.direction, edges: [] }
    group.edges.push({ edge, counterpart })
    groups.set(key, group)
  }
  return [...groups.values()]
    .map((group) => ({ ...group, edges: group.edges.sort((left, right) => left.counterpart.name.localeCompare(right.counterpart.name)) }))
    .sort((left, right) => left.direction === right.direction ? left.label.localeCompare(right.label) : left.direction === 'Upstream' ? -1 : 1)
}

/**
 * One CI in the dependency tree, together with the edge that reached it — the connector above a node
 * is labelled with that edge's verb, which is what turns a list of hops into a path somebody can read.
 */
export type DependencyTreeNode = {
  key: string
  node: CiGraphNode
  /** The relationship type of the edge walked to get here; null only at the root. */
  via: CiRelationshipType | null
  children: DependencyTreeNode[]
  /** True when this CI is already drawn higher up: a diamond or a cycle, shown once and not re-walked. */
  repeated: boolean
}

/**
 * The traversal response turned into a tree rooted at the open CI. The server returns nodes stamped
 * with a hop distance plus every edge between the CIs it reached, which says how far away a CI is but
 * not what route leads to it — this walks the edges in the traversal's own direction to recover that.
 *
 * A CI reachable two ways is drawn under the first route to it and marked `repeated` rather than
 * expanded again, so a diamond stays finite and a cycle terminates.
 */
export function buildDependencyTree(graph: CiGraph, root: CiGraphNode): DependencyTreeNode {
  const nodesById = new Map(graph.nodes.map((node) => [node.id, node]))
  nodesById.set(root.id, root)

  // Ancestors walk source → target ("this depends on that"); descendants walk the edges backwards.
  const upward = graph.direction === 'Ancestors'
  const nextFrom = new Map<string, { id: string; type: CiRelationshipType; edgeId: string }[]>()
  for (const edge of graph.edges) {
    const [from, to] = upward ? [edge.sourceCiId, edge.targetCiId] : [edge.targetCiId, edge.sourceCiId]
    if (!nodesById.has(from) || !nodesById.has(to)) continue
    nextFrom.set(from, [...(nextFrom.get(from) ?? []), { id: to, type: edge.type, edgeId: edge.id }])
  }

  const placed = new Set<string>([root.id])
  const walk = (node: CiGraphNode, via: CiRelationshipType | null, key: string): DependencyTreeNode => {
    const children = (nextFrom.get(node.id) ?? [])
      .map((step) => ({ step, child: nodesById.get(step.id)! }))
      .sort((left, right) => left.child.name.localeCompare(right.child.name))
      .filter(({ child }) => child.id !== root.id)
    const branches: DependencyTreeNode[] = []
    for (const { step, child } of children) {
      const childKey = `${key}/${step.edgeId}`
      if (placed.has(child.id)) {
        branches.push({ key: childKey, node: child, via: step.type, children: [], repeated: true })
        continue
      }
      placed.add(child.id)
      branches.push(walk(child, step.type, childKey))
    }
    return { key, node, via, children: branches, repeated: false }
  }

  return walk(root, null, root.id)
}

/** How many hops the deepest branch actually reaches, which is what "N levels shown" counts. */
export function dependencyTreeDepth(tree: DependencyTreeNode): number {
  return tree.children.length === 0 ? 0 : 1 + Math.max(...tree.children.map(dependencyTreeDepth))
}
