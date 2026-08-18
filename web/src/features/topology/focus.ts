import type { TopologyEdge, TopologyObservedLink } from '../../api/topology'

/**
 * Who each CI touches, both ways. Built once per graph and reused by every selection, so choosing a
 * node is a walk over a map rather than a scan of the edge list — the difference between O(hops ×
 * degree) and O(N) per click, which is what makes selection stay instant as the estate grows.
 */
export type Adjacency = {
  /** Undirected: everything a CI touches, whichever way the relationship points. */
  touching: Map<string, Set<string>>
  /** What this CI depends on — follow to walk toward the things it needs. */
  upstream: Map<string, Set<string>>
  /** What depends on this CI — follow to walk toward what breaks if it dies. */
  downstream: Map<string, Set<string>>
}

function add(map: Map<string, Set<string>>, from: string, to: string) {
  const existing = map.get(from)
  if (existing) existing.add(to)
  else map.set(from, new Set([to]))
}

/**
 * Observed links join `touching` but never `upstream`/`downstream`: a cable has no direction, and
 * treating one as a dependency would claim something nobody asserted — the same rule the layout
 * already follows when it refuses to layer on them.
 */
export function buildAdjacency(
  edges: readonly TopologyEdge[],
  observedLinks: readonly TopologyObservedLink[] = [],
): Adjacency {
  const touching = new Map<string, Set<string>>()
  const upstream = new Map<string, Set<string>>()
  const downstream = new Map<string, Set<string>>()

  for (const edge of edges) {
    add(touching, edge.sourceCiId, edge.targetCiId)
    add(touching, edge.targetCiId, edge.sourceCiId)
    add(upstream, edge.sourceCiId, edge.targetCiId)
    add(downstream, edge.targetCiId, edge.sourceCiId)
  }

  for (const link of observedLinks) {
    add(touching, link.sourceCiId, link.targetCiId)
    add(touching, link.targetCiId, link.sourceCiId)
  }

  return { touching, upstream, downstream }
}

/**
 * Every CI within `hops` of the root, mapped to how far away it is. The root itself is distance 0.
 *
 * Breadth-first, so the distance recorded is the shortest one — a node reachable in both one hop and
 * three is one hop away, and fading by distance has to agree with that or the picture lies about how
 * close something is.
 */
export function neighbourhood(
  adjacency: Adjacency,
  rootCiId: string,
  hops: number,
  direction: 'both' | 'upstream' | 'downstream' = 'both',
): Map<string, number> {
  const source = direction === 'both'
    ? adjacency.touching
    : direction === 'upstream' ? adjacency.upstream : adjacency.downstream

  const distances = new Map<string, number>([[rootCiId, 0]])
  if (hops <= 0) return distances

  let frontier = [rootCiId]
  for (let distance = 1; distance <= hops && frontier.length > 0; distance++) {
    const next: string[] = []
    for (const ciId of frontier) {
      for (const neighbour of source.get(ciId) ?? []) {
        if (distances.has(neighbour)) continue
        distances.set(neighbour, distance)
        next.push(neighbour)
      }
    }

    frontier = next
  }

  return distances
}

/** True when an edge has the given CI at either end — what "directly connected" means for styling. */
export function touchesCi(edge: { source: string; target: string }, ciId: string): boolean {
  return edge.source === ciId || edge.target === ciId
}

/**
 * How prominent something should be drawn for the current selection.
 *
 * `selected` is the CI itself, `related` is within the neighbourhood, `muted` is everything else.
 * Nothing is ever `hidden` unless Focus Mode is on: §5's rule is that unrelated topology fades but
 * stays on the map, because "what else is out there" is half of what an operator is judging.
 */
export type Emphasis = 'selected' | 'related' | 'muted'

export function emphasisFor(
  ciId: string,
  selectedCiId: string | null,
  neighbours: Map<string, number> | null,
): Emphasis {
  if (selectedCiId === null) return 'related'
  if (ciId === selectedCiId) return 'selected'
  return neighbours?.has(ciId) ? 'related' : 'muted'
}

/** The default Focus Mode radius. Two hops is far enough to show a cause and its effect. */
export const defaultFocusHops = 2
