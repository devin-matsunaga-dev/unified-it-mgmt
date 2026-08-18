import { describe, expect, it } from 'vitest'
import type { Node } from '@xyflow/react'
import type { TopologyNode } from '../../api/topology'
import { siteBands } from './SiteBands'
import type { TopologyNodeData } from './TopologyNodeCard'

function ci(ciId: string, siteName: string | null): TopologyNode {
  return {
    ciId, name: ciId, type: 'Server', lifecycleState: 'Deployed', isActive: true,
    siteName, address: null, lastSeenByDiscoveryAt: null, networkRole: null,
  }
}

function node(ciId: string, siteName: string | null, x: number, y: number, hidden = false): Node<TopologyNodeData> {
  return {
    id: ciId,
    position: { x, y },
    hidden,
    data: {
      ci: ci(ciId, siteName), status: null, deviceId: null, pinned: false,
      emphasis: 'related', group: null, openAlerts: 0, correlation: null,
    },
    measured: { width: 200, height: 80 },
  }
}

describe('siteBands', () => {
  it('encloses every node of a site and keeps sites apart', () => {
    const bands = siteBands([
      node('a', 'HQ', 0, 0), node('b', 'HQ', 400, 200), node('c', 'BR1', 1000, 0),
    ])

    expect(bands.map((band) => band.siteName)).toEqual(['BR1', 'HQ'])
    const hq = bands.find((band) => band.siteName === 'HQ')!
    // Left/top of the leftmost node, minus padding; wide enough to reach the far node's right edge.
    expect(hq.x).toBeLessThan(0)
    expect(hq.x + hq.width).toBeGreaterThan(600)
    expect(hq.y + hq.height).toBeGreaterThan(280)
  })

  /** §8: a site is only ever read from the CMDB, never inferred from a name. */
  it('ignores CIs with no site recorded', () => {
    expect(siteBands([node('a', null, 0, 0), node('b', '', 100, 0)])).toEqual([])
  })

  /** A band around one machine still says where the boundary is. */
  it('draws a band for a site with a single node', () => {
    expect(siteBands([node('a', 'HQ', 0, 0)])).toHaveLength(1)
  })

  /** Focus Mode hides nodes; a band stretching to something invisible would be a lie. */
  it('excludes hidden nodes from the boundary', () => {
    const withHidden = siteBands([node('a', 'HQ', 0, 0), node('far', 'HQ', 5000, 0, true)])

    expect(withHidden[0].width).toBeLessThan(1000)
  })

  it('follows a node that has been dragged', () => {
    const before = siteBands([node('a', 'HQ', 0, 0), node('b', 'HQ', 200, 0)])
    const after = siteBands([node('a', 'HQ', 0, 0), node('b', 'HQ', 900, 0)])

    expect(after[0].width).toBeGreaterThan(before[0].width)
  })
})
