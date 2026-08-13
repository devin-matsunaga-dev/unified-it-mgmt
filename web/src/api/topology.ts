import { apiRequest } from './client'
import type { CiLifecycleState, CiRelationshipType, CiType } from './assets'

/** Why a neighbour report could not be placed against a CI. */
export type TopologyResolutionFailure = 'NoCandidate' | 'Ambiguous' | 'NoIdentity'

/**
 * One CI on the map. It deliberately carries no health: colour comes from the monitoring status
 * board, joined in the browser on `ciId`. A CI nothing monitors has no status at all, and that is a
 * fact about the estate rather than a gap in this payload.
 */
export type TopologyNode = {
  ciId: string
  name: string
  type: CiType
  lifecycleState: CiLifecycleState
  isActive: boolean
  siteName: string | null
  address: string | null
  lastSeenByDiscoveryAt: string | null
}

/** A relationship somebody asserted. `observedByDiscovery` says whether a scan agreed with it. */
export type TopologyEdge = {
  id: string
  sourceCiId: string
  targetCiId: string
  type: CiRelationshipType
  description: string | null
  observedByDiscovery: boolean
}

/**
 * A link a scan saw over LLDP or CDP, with both ends resolved to CIs. Never a relationship: nothing
 * writes these to the CMDB, which is what leaves WP-4.6 a drift to report.
 */
export type TopologyObservedLink = {
  id: string
  sourceCiId: string
  targetCiId: string
  protocols: string[]
  sourcePort: string | null
  targetPort: string | null
  confirmedByBothEnds: boolean
  matchesAssertedEdge: boolean
}

export type TopologyUnresolvedNeighbour = {
  reportedByCiId: string
  reportedByCiName: string
  protocol: string
  localPort: string | null
  remoteSystemName: string | null
  remotePort: string | null
  remoteAddress: string | null
  reason: TopologyResolutionFailure
}

export type Topology = {
  nodes: TopologyNode[]
  edges: TopologyEdge[]
  observedLinks: TopologyObservedLink[]
  unresolvedNeighbours: TopologyUnresolvedNeighbour[]
  nodeLimit: number
  nodeLimitReached: boolean
}

export type TopologyMapNode = { ciId: string; x: number; y: number }

export type TopologyMapSummary = {
  id: string
  name: string
  description: string | null
  pinnedNodeCount: number
  createdBy: string
  createdAt: string
  updatedBy: string | null
  updatedAt: string
}

export type TopologyMap = Omit<TopologyMapSummary, 'pinnedNodeCount'> & { nodes: TopologyMapNode[] }

export type SaveTopologyMapInput = {
  name: string
  description?: string | null
  nodes: TopologyMapNode[]
}

const unresolvedReasons: Record<TopologyResolutionFailure, string> = {
  NoCandidate: 'No CI is named that or records that address',
  Ambiguous: 'Two CIs answer to it',
  NoIdentity: 'The device named nothing',
}

export function unresolvedReasonLabel(reason: string) {
  return unresolvedReasons[reason as TopologyResolutionFailure] ?? reason
}

export const topologyApi = {
  get: (types?: CiType[]) =>
    apiRequest<Topology>(`/api/topology${types?.length ? `?types=${types.join(',')}` : ''}`),

  listMaps: () => apiRequest<TopologyMapSummary[]>('/api/topology-maps'),

  getMap: (id: string) => apiRequest<TopologyMap>(`/api/topology-maps/${id}`),

  createMap: (input: SaveTopologyMapInput) =>
    apiRequest<TopologyMap>('/api/topology-maps', { method: 'POST', body: JSON.stringify(input) }),

  updateMap: (id: string, input: SaveTopologyMapInput) =>
    apiRequest<TopologyMap>(`/api/topology-maps/${id}`, { method: 'PUT', body: JSON.stringify(input) }),

  deleteMap: (id: string) =>
    apiRequest<void>(`/api/topology-maps/${id}`, { method: 'DELETE' }),
}
