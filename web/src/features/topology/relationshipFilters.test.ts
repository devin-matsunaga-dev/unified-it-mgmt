import { describe, expect, it } from 'vitest'
import type { CiRelationshipType } from '../../api/assets'
import type { TopologyEdge, TopologyNode, TopologyObservedLink } from '../../api/topology'
import {
  availableRelationshipFilters, defaultRelationshipFilter, relationshipFilterById,
  relationshipFilters, sitesOf,
} from './relationshipFilters'

function edge(type: CiRelationshipType): TopologyEdge {
  return {
    id: `edge-${type}`, sourceCiId: 'a', targetCiId: 'b', type,
    description: null, observedByDiscovery: false,
  }
}

function link(matchesAssertedEdge = false): TopologyObservedLink {
  return {
    id: 'observed:a:b', sourceCiId: 'a', targetCiId: 'b', protocols: ['lldp'],
    sourcePort: null, targetPort: null, confirmedByBothEnds: false, matchesAssertedEdge,
  }
}

function node(ciId: string, siteName: string | null): TopologyNode {
  return {
    ciId, name: ciId, type: 'Server', lifecycleState: 'Deployed', isActive: true,
    siteName, address: null, lastSeenByDiscoveryAt: null, networkRole: null,
  }
}

describe('relationshipFilters', () => {
  /** §7: built from the model that exists — no invented relationship types. */
  it('offers exactly the four recorded types plus the recorded/discovered split', () => {
    expect(relationshipFilters.map((filter) => filter.id))
      .toEqual(['all', 'recorded', 'discovered', 'ConnectsTo', 'DependsOn', 'RunsOn', 'HostedOn'])
  })

  it('draws everything by default', () => {
    expect(defaultRelationshipFilter.id).toBe('all')
    expect(defaultRelationshipFilter.edge(edge('RunsOn'))).toBe(true)
    expect(defaultRelationshipFilter.observed(link())).toBe(true)
  })

  /** The distinction WP-4.6's drift report exists for: asserted versus merely seen. */
  it('separates what the CMDB records from what only a scan saw', () => {
    const recorded = relationshipFilterById('recorded')
    expect(recorded.edge(edge('ConnectsTo'))).toBe(true)
    expect(recorded.observed(link())).toBe(false)

    const discovered = relationshipFilterById('discovered')
    expect(discovered.edge(edge('ConnectsTo'))).toBe(false)
    expect(discovered.observed(link())).toBe(true)
  })

  it('narrows to one relationship type', () => {
    const dependsOn = relationshipFilterById('DependsOn')
    expect(dependsOn.edge(edge('DependsOn'))).toBe(true)
    expect(dependsOn.edge(edge('RunsOn'))).toBe(false)
    expect(dependsOn.observed(link())).toBe(false)
  })

  it('falls back to All for an id nobody offers', () => {
    expect(relationshipFilterById('nonsense')).toBe(defaultRelationshipFilter)
  })
})

describe('availableRelationshipFilters', () => {
  /** A cut with nothing behind it is a dead end, so it is not offered. */
  it('offers only the cuts that would change the picture', () => {
    const available = availableRelationshipFilters([edge('ConnectsTo'), edge('DependsOn')], [])

    expect(available.map((filter) => filter.id)).toEqual(['all', 'recorded', 'ConnectsTo', 'DependsOn'])
  })

  it('always offers All, even for an empty graph', () => {
    expect(availableRelationshipFilters([], []).map((filter) => filter.id)).toEqual(['all'])
  })

  /**
   * An observation the CMDB already records is folded into the asserted edge and never drawn on its
   * own, so it must not make "Discovered only" look available.
   */
  it('ignores an observation that matches a recorded relationship', () => {
    expect(availableRelationshipFilters([], [link(true)]).map((filter) => filter.id)).toEqual(['all'])
    expect(availableRelationshipFilters([], [link(false)]).map((filter) => filter.id))
      .toEqual(['all', 'discovered'])
  })
})

describe('sitesOf', () => {
  it('lists each site once, in name order', () => {
    expect(sitesOf([node('a', 'HQ'), node('b', 'BR1'), node('c', 'HQ')])).toEqual(['BR1', 'HQ'])
  })

  /** Most of a CMDB has no site recorded, and §8 forbids inferring one from a name. */
  it('skips CIs with no site rather than inventing one', () => {
    expect(sitesOf([node('a', null), node('b', ''), node('c', 'HQ')])).toEqual(['HQ'])
  })
})
