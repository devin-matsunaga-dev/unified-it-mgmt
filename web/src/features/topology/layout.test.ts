import { describe, expect, it } from 'vitest'
import type { TopologyEdge, TopologyNode, TopologyObservedLink } from '../../api/topology'
import { autoLayout, mergePins, resolveLayout } from './layout'

function node(ciId: string, name = ciId): TopologyNode {
  return {
    ciId,
    name,
    type: 'NetworkDevice',
    lifecycleState: 'Deployed',
    isActive: true,
    siteName: null,
    address: null,
    lastSeenByDiscoveryAt: null,
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

describe('autoLayout', () => {
  /**
   * The direction of an edge is the whole meaning of the picture: the source needs the target, so
   * the target belongs above it. Layering falls out of that rather than out of CI type.
   */
  it('stacks a dependency chain with what depends on nothing at the top', () => {
    const positions = autoLayout(
      [node('router'), node('switch'), node('host'), node('vm')],
      [edge('switch', 'router'), edge('host', 'switch'), edge('vm', 'host')])

    const y = (id: string) => positions.get(id)!.y
    expect(y('router')).toBeLessThan(y('switch'))
    expect(y('switch')).toBeLessThan(y('host'))
    expect(y('host')).toBeLessThan(y('vm'))
  })

  it('puts siblings on one row and spreads them apart', () => {
    const positions = autoLayout(
      [node('router'), node('sw-a'), node('sw-b')],
      [edge('sw-a', 'router'), edge('sw-b', 'router')])

    expect(positions.get('sw-a')!.y).toBe(positions.get('sw-b')!.y)
    expect(positions.get('sw-a')!.x).not.toBe(positions.get('sw-b')!.x)
  })

  /**
   * WP-2.3 accepts mutually dependent CIs deliberately, so the layout has to survive one. Without
   * the path guard this walk does not terminate.
   */
  it('terminates on a cycle instead of recursing forever', () => {
    const positions = autoLayout(
      [node('a'), node('b'), node('c')],
      [edge('a', 'b'), edge('b', 'c'), edge('c', 'a')])

    expect(positions.size).toBe(3)
    for (const id of ['a', 'b', 'c']) expect(Number.isFinite(positions.get(id)!.y)).toBe(true)
  })

  /**
   * An observed link is undirected — a cable has no "up" — so it must not create a layer. Two core
   * switches patched together stay side by side rather than one being stacked under the other.
   */
  it('does not let an observed link stack one node under another', () => {
    const positions = autoLayout(
      [node('sw-a'), node('sw-b')],
      [],
      [link('sw-a', 'sw-b')])

    expect(positions.get('sw-a')!.y).toBe(positions.get('sw-b')!.y)
  })

  it('ignores an edge whose other end is not on the map', () => {
    const positions = autoLayout([node('switch')], [edge('switch', 'router-not-drawn')])

    expect(positions.size).toBe(1)
    expect(positions.get('switch')!.y).toBe(0)
  })

  /** Two runs of the same estate must draw the same picture, or every reload looks like a change. */
  it('is deterministic', () => {
    const nodes = [node('router'), node('sw-b'), node('sw-a'), node('host')]
    const edges = [edge('sw-a', 'router'), edge('sw-b', 'router'), edge('host', 'sw-a')]

    expect([...autoLayout(nodes, edges)]).toEqual([...autoLayout(nodes, edges)])
  })
})

describe('resolveLayout', () => {
  it('lets a pin override the automatic position', () => {
    const nodes = [node('router'), node('switch')]
    const edges = [edge('switch', 'router')]

    const positions = resolveLayout(nodes, edges, [], [{ ciId: 'switch', x: 900, y: -120 }])

    expect(positions.get('switch')).toEqual({ x: 900, y: -120 })
    expect(positions.get('router')).toEqual(autoLayout(nodes, edges).get('router'))
  })

  it('ignores a pin for a CI the map is not drawing', () => {
    const positions = resolveLayout([node('router')], [], [], [{ ciId: 'gone', x: 5, y: 5 }])

    expect(positions.has('gone')).toBe(false)
  })
})

describe('mergePins', () => {
  /**
   * Saving while a type filter is applied must not un-pin everything the filter hides. A save states
   * where the visible nodes are; it says nothing about the ones off screen.
   */
  it('keeps the positions of nodes the current filter hides', () => {
    const merged = mergePins(
      [{ ciId: 'switch', x: 10, y: 10 }],
      [{ ciId: 'switch', x: 0, y: 0 }, { ciId: 'server', x: 99, y: 99 }],
      new Set(['switch']))

    expect(merged).toContainEqual({ ciId: 'server', x: 99, y: 99 })
    expect(merged).toContainEqual({ ciId: 'switch', x: 10, y: 10 })
    expect(merged).toHaveLength(2)
  })

  it('drops a pin for a visible node that is no longer on the canvas', () => {
    const merged = mergePins(
      [{ ciId: 'switch', x: 10, y: 10 }],
      [{ ciId: 'switch', x: 0, y: 0 }, { ciId: 'router', x: 1, y: 1 }],
      new Set(['switch', 'router']))

    expect(merged).toEqual([{ ciId: 'switch', x: 10, y: 10 }])
  })
})
