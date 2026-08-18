import { describe, expect, it } from 'vitest'
import type { TopologyEdge, TopologyObservedLink } from '../../api/topology'
import { buildAdjacency, emphasisFor, neighbourhood, touchesCi } from './focus'

function edge(source: string, target: string): TopologyEdge {
  return {
    id: `${source}->${target}`,
    sourceCiId: source,
    targetCiId: target,
    type: 'DependsOn',
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

/** router ← switch ← server ← app, plus a second app on the same server. */
const chain = [edge('switch', 'router'), edge('server', 'switch'), edge('app', 'server'), edge('report', 'server')]

describe('buildAdjacency', () => {
  it('records both directions of a relationship separately', () => {
    const adjacency = buildAdjacency([edge('server', 'switch')])

    expect([...adjacency.upstream.get('server')!]).toEqual(['switch'])
    expect([...adjacency.downstream.get('switch')!]).toEqual(['server'])
    expect([...adjacency.touching.get('server')!]).toEqual(['switch'])
    expect([...adjacency.touching.get('switch')!]).toEqual(['server'])
  })

  /**
   * A cable has no "up". Letting an observed link into the directed maps would claim a dependency
   * nobody asserted, which is the same rule the layout follows when it refuses to layer on them.
   */
  it('lets an observed link touch without giving it a direction', () => {
    const adjacency = buildAdjacency([], [link('switch-a', 'switch-b')])

    expect([...adjacency.touching.get('switch-a')!]).toEqual(['switch-b'])
    expect(adjacency.upstream.get('switch-a')).toBeUndefined()
    expect(adjacency.downstream.get('switch-a')).toBeUndefined()
  })
})

describe('neighbourhood', () => {
  it('includes the root at distance zero', () => {
    expect(neighbourhood(buildAdjacency(chain), 'server', 0)).toEqual(new Map([['server', 0]]))
  })

  it('reaches exactly the requested number of hops and no further', () => {
    const adjacency = buildAdjacency(chain)

    const oneHop = neighbourhood(adjacency, 'server', 1)
    expect([...oneHop.keys()].sort()).toEqual(['app', 'report', 'server', 'switch'])

    const twoHops = neighbourhood(adjacency, 'server', 2)
    expect([...twoHops.keys()].sort()).toEqual(['app', 'report', 'router', 'server', 'switch'])
    expect(twoHops.get('router')).toBe(2)
  })

  /** Upstream answers "what does this need"; downstream answers "what breaks if it dies". */
  it('walks upstream and downstream separately', () => {
    const adjacency = buildAdjacency(chain)

    expect([...neighbourhood(adjacency, 'server', 2, 'upstream').keys()].sort())
      .toEqual(['router', 'server', 'switch'])
    expect([...neighbourhood(adjacency, 'server', 2, 'downstream').keys()].sort())
      .toEqual(['app', 'report', 'server'])
  })

  /**
   * Breadth-first, so a node reachable by both a short and a long path records the short one. Fading
   * by distance has to agree with that or the picture says something is further away than it is.
   */
  it('records the shortest distance when two paths reach the same CI', () => {
    const adjacency = buildAdjacency([
      edge('a', 'root'), edge('b', 'a'), edge('b', 'root'),
    ])

    expect(neighbourhood(adjacency, 'root', 3).get('b')).toBe(1)
  })

  it('terminates on a cycle', () => {
    const adjacency = buildAdjacency([edge('a', 'b'), edge('b', 'c'), edge('c', 'a')])

    expect([...neighbourhood(adjacency, 'a', 10).keys()].sort()).toEqual(['a', 'b', 'c'])
  })

  it('returns just the root for a CI nothing touches', () => {
    expect([...neighbourhood(buildAdjacency(chain), 'orphan', 2).keys()]).toEqual(['orphan'])
  })
})

describe('emphasisFor', () => {
  /** With nothing selected the whole map is drawn normally — no permanent fading. */
  it('treats everything as related when nothing is selected', () => {
    expect(emphasisFor('anything', null, null)).toBe('related')
  })

  it('separates the selected CI from its neighbours and from everything else', () => {
    const neighbours = neighbourhood(buildAdjacency(chain), 'server', 1)

    expect(emphasisFor('server', 'server', neighbours)).toBe('selected')
    expect(emphasisFor('switch', 'server', neighbours)).toBe('related')
    expect(emphasisFor('router', 'server', neighbours)).toBe('muted')
  })
})

describe('touchesCi', () => {
  it('matches an edge at either end', () => {
    expect(touchesCi({ source: 'a', target: 'b' }, 'a')).toBe(true)
    expect(touchesCi({ source: 'a', target: 'b' }, 'b')).toBe(true)
    expect(touchesCi({ source: 'a', target: 'b' }, 'c')).toBe(false)
  })
})
