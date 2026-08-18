import type { CiRelationshipType } from '../../api/assets'
import type { TopologyEdge, TopologyNode, TopologyObservedLink } from '../../api/topology'

/**
 * The relationship cuts (§7), built from the model that exists rather than an invented vocabulary:
 * the four <c>CiRelationshipType</c> values, plus the recorded/discovered split the map already
 * draws as solid and dashed.
 */
export type RelationshipFilter = {
  id: string
  label: string
  /** Whether an asserted relationship of this type is drawn. */
  edge: (edge: TopologyEdge) => boolean
  /** Whether a discovery-only observation is drawn. */
  observed: (link: TopologyObservedLink) => boolean
}

const anyEdge = () => true
const noEdge = () => false

function ofType(type: CiRelationshipType): RelationshipFilter['edge'] {
  return (edge) => edge.type === type
}

export const relationshipFilters: readonly RelationshipFilter[] = [
  { id: 'all', label: 'All relationships', edge: anyEdge, observed: anyEdge },
  // Recorded is what the CMDB asserts; discovered is what a scan saw and nobody wrote down. The
  // difference is the whole point of WP-4.6's drift report, so it earns a cut of its own.
  { id: 'recorded', label: 'Recorded only', edge: anyEdge, observed: noEdge },
  { id: 'discovered', label: 'Discovered only', edge: noEdge, observed: anyEdge },
  { id: 'ConnectsTo', label: 'Connects to', edge: ofType('ConnectsTo'), observed: noEdge },
  { id: 'DependsOn', label: 'Depends on', edge: ofType('DependsOn'), observed: noEdge },
  { id: 'RunsOn', label: 'Runs on', edge: ofType('RunsOn'), observed: noEdge },
  { id: 'HostedOn', label: 'Hosted on', edge: ofType('HostedOn'), observed: noEdge },
]

export const defaultRelationshipFilter = relationshipFilters[0]

export function relationshipFilterById(id: string): RelationshipFilter {
  return relationshipFilters.find((filter) => filter.id === id) ?? defaultRelationshipFilter
}

/**
 * Which relationship cuts have anything to show for the graph on screen.
 *
 * Offering "Hosted on" against an estate with no such relationship is a dead end that says nothing;
 * the list is narrowed to the cuts that would actually change the picture. "All" is always offered.
 */
export function availableRelationshipFilters(
  edges: readonly TopologyEdge[],
  observedLinks: readonly TopologyObservedLink[],
): RelationshipFilter[] {
  const drawnObserved = observedLinks.filter((link) => !link.matchesAssertedEdge)
  return relationshipFilters.filter((filter) => filter.id === 'all'
    || edges.some(filter.edge)
    || drawnObserved.some(filter.observed))
}

/** The site each node belongs to, in the order the sites first appear. Nodes without one are skipped. */
export function sitesOf(nodes: readonly TopologyNode[]): string[] {
  const seen = new Set<string>()
  for (const node of nodes) {
    if (node.siteName !== null && node.siteName !== '') seen.add(node.siteName)
  }

  return [...seen].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }))
}
