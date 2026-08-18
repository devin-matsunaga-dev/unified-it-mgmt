import { describe, expect, it } from 'vitest'
import type { CiType } from '../../api/assets'
import type { TopologyEdge, TopologyNode, TopologyObservedLink } from '../../api/topology'
import {
  collapseEndpoints, endpointGroupId, findEndpointGroups, isEndpointGroupId,
} from './endpointGroups'

function node(ciId: string, type: CiType = 'Hardware'): TopologyNode {
  return {
    ciId,
    name: ciId,
    type,
    lifecycleState: 'Deployed',
    isActive: true,
    siteName: 'HQ',
    address: null,
    lastSeenByDiscoveryAt: null,
    networkRole: null,
  }
}

function edge(source: string, target: string): TopologyEdge {
  return {
    id: `${source}->${target}`,
    sourceCiId: source,
    targetCiId: target,
    type: 'ConnectsTo',
    description: null,
    observedByDiscovery: false,
  }
}

function link(source: string, target: string): TopologyObservedLink {
  return {
    id: `observed:${source}:${target}`,
    sourceCiId: source,
    targetCiId: target,
    protocols: ['lldp'],
    sourcePort: null,
    targetPort: null,
    confirmedByBothEnds: false,
    matchesAssertedEdge: false,
  }
}

/** One access switch with four laptops on it, plus a router above the switch. */
const estate = {
  nodes: [
    node('router', 'NetworkDevice'), node('switch', 'NetworkDevice'),
    node('lt-1'), node('lt-2'), node('lt-3'), node('lt-4'),
  ],
  edges: [
    edge('switch', 'router'),
    edge('lt-1', 'switch'), edge('lt-2', 'switch'), edge('lt-3', 'switch'), edge('lt-4', 'switch'),
  ],
}

describe('findEndpointGroups', () => {
  it('groups the endpoints hanging off one switch', () => {
    const groups = findEndpointGroups(estate.nodes, estate.edges)

    expect(groups).toHaveLength(1)
    expect(groups[0].parentCiId).toBe('switch')
    expect(groups[0].memberCiIds).toEqual(['lt-1', 'lt-2', 'lt-3', 'lt-4'])
  })

  /** Two endpoints are not clutter; replacing them with a node saying "2 endpoints" is. */
  it('leaves a handful of endpoints alone', () => {
    const nodes = [node('switch', 'NetworkDevice'), node('lt-1'), node('lt-2')]
    const edges = [edge('lt-1', 'switch'), edge('lt-2', 'switch')]

    expect(findEndpointGroups(nodes, edges)).toEqual([])
  })

  /**
   * A Hardware CI something else depends on is structural, not a desk machine, and hiding it would
   * be hiding topology rather than tidying it.
   */
  it('never groups a CI that something depends on', () => {
    const nodes = [...estate.nodes, node('shelf'), node('app', 'Software')]
    const edges = [...estate.edges, edge('shelf', 'switch'), edge('app', 'shelf')]

    const groups = findEndpointGroups(nodes, edges)

    expect(groups[0].memberCiIds).not.toContain('shelf')
  })

  /** Two uplinks means it is doing something; one is what makes it a leaf on a switch. */
  it('never groups a CI with more than one neighbour', () => {
    const nodes = [...estate.nodes, node('switch-b', 'NetworkDevice')]
    const edges = [...estate.edges, edge('lt-1', 'switch-b')]

    expect(findEndpointGroups(nodes, edges)[0].memberCiIds).not.toContain('lt-1')
  })

  it('never groups a CI that is not Hardware', () => {
    const nodes = [node('switch', 'NetworkDevice'), node('vm-1', 'Virtual'), node('vm-2', 'Virtual'), node('vm-3', 'Virtual')]
    const edges = [edge('vm-1', 'switch'), edge('vm-2', 'switch'), edge('vm-3', 'switch')]

    expect(findEndpointGroups(nodes, edges)).toEqual([])
  })

  it('never groups under something that is not a network device', () => {
    const nodes = [node('host', 'Server'), node('a'), node('b'), node('c')]
    const edges = [edge('a', 'host'), edge('b', 'host'), edge('c', 'host')]

    expect(findEndpointGroups(nodes, edges)).toEqual([])
  })

  /** An endpoint only a scan saw on a switch is still an endpoint on that switch. */
  it('counts an observed link as the single neighbour', () => {
    const nodes = [node('switch', 'NetworkDevice'), node('lt-1'), node('lt-2'), node('lt-3')]
    const links = [link('lt-1', 'switch'), link('lt-2', 'switch'), link('lt-3', 'switch')]

    expect(findEndpointGroups(nodes, [], links)[0].memberCiIds).toEqual(['lt-1', 'lt-2', 'lt-3'])
  })

  it('keeps groups for different switches apart', () => {
    const nodes = [
      node('sw-a', 'NetworkDevice'), node('sw-b', 'NetworkDevice'),
      node('a1'), node('a2'), node('a3'), node('b1'), node('b2'), node('b3'),
    ]
    const edges = [
      edge('a1', 'sw-a'), edge('a2', 'sw-a'), edge('a3', 'sw-a'),
      edge('b1', 'sw-b'), edge('b2', 'sw-b'), edge('b3', 'sw-b'),
    ]

    expect(findEndpointGroups(nodes, edges).map((group) => group.parentCiId)).toEqual(['sw-a', 'sw-b'])
  })
})

describe('collapseEndpoints', () => {
  it('replaces the endpoints with one node and one uplink', () => {
    const display = collapseEndpoints(estate.nodes, estate.edges, [], new Set())

    expect(display.nodes.map((item) => item.ciId).sort())
      .toEqual([endpointGroupId('switch'), 'router', 'switch'])
    const group = display.nodes.find((item) => isEndpointGroupId(item.ciId))!
    expect(group.name).toBe('4 endpoints')
    expect(display.edges.map((item) => item.id).sort())
      .toEqual([`${endpointGroupId('switch')}:uplink`, 'switch->router'])
  })

  /** §2: the data is never lost — expanding restores exactly what the server sent. */
  it('restores every endpoint and every edge when the group is expanded', () => {
    const display = collapseEndpoints(estate.nodes, estate.edges, [], new Set([endpointGroupId('switch')]))

    expect(display.nodes.map((item) => item.ciId).sort()).toEqual(estate.nodes.map((item) => item.ciId).sort())
    expect(display.edges.map((item) => item.id).sort()).toEqual(estate.edges.map((item) => item.id).sort())
    expect(display.collapsedInto.size).toBe(0)
    // The group is still reported, so the switch can still offer to collapse it again.
    expect(display.groups).toHaveLength(1)
  })

  it('maps each folded endpoint back to its group', () => {
    const display = collapseEndpoints(estate.nodes, estate.edges, [], new Set())

    expect(display.collapsedInto.get('lt-1')?.parentCiId).toBe('switch')
    expect(display.collapsedInto.has('router')).toBe(false)
  })

  it('leaves a graph with nothing to group untouched', () => {
    const nodes = [node('router', 'NetworkDevice'), node('switch', 'NetworkDevice')]
    const edges = [edge('switch', 'router')]

    const display = collapseEndpoints(nodes, edges, [], new Set())

    expect(display.nodes).toHaveLength(2)
    expect(display.edges).toHaveLength(1)
    expect(display.groups).toEqual([])
  })

  /**
   * The group id must never be mistaken for a CI: the saved-map payload is built from what is on the
   * canvas, and posting one as a pinned CI would send the server an id no CI has.
   */
  it('marks a group id as synthetic', () => {
    expect(isEndpointGroupId(endpointGroupId('switch'))).toBe(true)
    expect(isEndpointGroupId('switch')).toBe(false)
  })
})
