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
    networkRole: null,
  }
}

function roled(ciId: string, networkRole: string): TopologyNode {
  return { ...node(ciId), networkRole }
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

describe('autoLayout crossing reduction', () => {
  /**
   * Counts pairs of edges that cross when drawn between adjacent layers. The alternating sweeps
   * exist to bring this down; a single downward pass leaves the leaf layers ordered by whatever
   * their parents happened to do, which is what made the real map rake.
   */
  function crossings(nodes: TopologyNode[], edges: TopologyEdge[]): number {
    const positions = autoLayout(nodes, edges)
    const drawn = edges.map((item) => ({
      from: positions.get(item.sourceCiId)!,
      to: positions.get(item.targetCiId)!,
    }))

    let total = 0
    for (let a = 0; a < drawn.length; a++) {
      for (let b = a + 1; b < drawn.length; b++) {
        const one = drawn[a]
        const two = drawn[b]
        if (one.from.y !== two.from.y || one.to.y !== two.to.y) continue
        if ((one.from.x - two.from.x) * (one.to.x - two.to.x) < 0) total++
      }
    }

    return total
  }

  /**
   * Two hosts whose VMs are named so that alphabetical order interleaves them across parents:
   * P and R belong to the B host, Q and S to the A host. Seeded by name alone every one of those
   * edges crosses its neighbour. Only a sweep that pulls each VM toward its own parent undoes it.
   */
  it('orders a layer so that children sit under their own parent', () => {
    const nodes = [
      node('switch', 'Switch'),
      node('host-a', 'A host'), node('host-b', 'B host'),
      node('vm-p', 'P vm'), node('vm-q', 'Q vm'), node('vm-r', 'R vm'), node('vm-s', 'S vm'),
    ]
    const edges = [
      edge('host-a', 'switch'), edge('host-b', 'switch'),
      edge('vm-p', 'host-b'), edge('vm-q', 'host-a'),
      edge('vm-r', 'host-b'), edge('vm-s', 'host-a'),
    ]

    expect(crossings(nodes, edges)).toBe(0)
  })

  /** The sweeps must settle rather than oscillate, or the same graph would draw differently twice. */
  it('reaches the same arrangement every time it is run', () => {
    const nodes = [
      node('root'),
      node('mid-a', 'A mid'), node('mid-b', 'B mid'), node('mid-c', 'C mid'),
      node('leaf-1', 'Z leaf'), node('leaf-2', 'Y leaf'), node('leaf-3', 'X leaf'),
    ]
    const edges = [
      edge('mid-a', 'root'), edge('mid-b', 'root'), edge('mid-c', 'root'),
      edge('leaf-1', 'mid-c'), edge('leaf-2', 'mid-b'), edge('leaf-3', 'mid-a'),
    ]

    const once = autoLayout(nodes, edges)
    const twice = autoLayout(nodes, edges)

    expect([...twice.entries()]).toEqual([...once.entries()])
    expect(crossings(nodes, edges)).toBe(0)
  })
})

describe('autoLayout with recorded network roles', () => {
  const rowOf = (positions: Map<string, { x: number; y: number }>, ciId: string) => positions.get(ciId)!.y

  /**
   * §9: a role is authoritative about the hierarchy. With no relationship between them, dependency
   * depth alone would put an edge router and a core switch on the same row.
   */
  it('orders unrelated devices by their role', () => {
    const positions = autoLayout(
      [roled('rtr', 'Edge'), roled('core', 'Core'), roled('acc', 'Access')], [])

    expect(rowOf(positions, 'rtr')).toBeLessThan(rowOf(positions, 'core'))
    expect(rowOf(positions, 'core')).toBeLessThan(rowOf(positions, 'acc'))
  })

  /**
   * The role is a floor, not a replacement: a dependency can push a device further down, because a
   * switch behind two routers really does belong below both.
   */
  it('lets a dependency push a device below its role floor', () => {
    const positions = autoLayout(
      [roled('rtr', 'Edge'), roled('core', 'Core'), roled('other', 'Edge')],
      [edge('core', 'rtr'), edge('other', 'core')])

    // 'other' is an Edge device, floor 0, but it depends on a Core switch and must sit under it.
    expect(rowOf(positions, 'other')).toBeGreaterThan(rowOf(positions, 'core'))
  })

  /** A CMDB that has not filled the field in must lay out exactly as it did before. */
  it('changes nothing when no device records a role', () => {
    const nodes = [node('a'), node('b'), node('c')]
    const edges = [edge('b', 'a'), edge('c', 'b')]

    const positions = autoLayout(nodes, edges)

    expect(rowOf(positions, 'a')).toBeLessThan(rowOf(positions, 'b'))
    expect(rowOf(positions, 'b')).toBeLessThan(rowOf(positions, 'c'))
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
