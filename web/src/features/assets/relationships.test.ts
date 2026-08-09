import { describe, expect, it } from 'vitest'
import type { CiGraph, CiGraphNode, CiRelationship, CiRelationshipType } from '../../api/assets'
import {
  buildDependencyTree, ciRelationshipVerb, dependencyTreeDepth, describeRelationship,
  groupDirectRelationships, relationshipCounterpart, relationshipGroupLabel,
} from './relationships'

const edge: CiRelationship = {
  id: 'edge-1',
  sourceCiId: 'ci-vm', sourceCiName: 'vm-payroll', sourceCiType: 'Virtual',
  targetCiId: 'ci-host', targetCiName: 'esx-01', targetCiType: 'Server',
  type: 'RunsOn', description: null, createdBy: 'technician1', createdAt: '2026-08-07T09:00:00Z',
}

describe('ciRelationshipVerb', () => {
  it('turns each type into the verb that makes an edge a sentence', () => {
    expect(ciRelationshipVerb('RunsOn')).toBe('runs on')
    expect(ciRelationshipVerb('HostedOn')).toBe('is hosted on')
  })

  // The card renders whatever the API returns; a type this build has not heard of must not blank the row.
  it('falls back to the raw value for an unknown type', () => {
    expect(ciRelationshipVerb('BackedUpBy')).toBe('BackedUpBy')
  })
})

describe('relationshipCounterpart', () => {
  it('reads the target as upstream when the open CI is the source', () => {
    expect(relationshipCounterpart(edge, 'ci-vm'))
      .toEqual({ direction: 'Upstream', ciId: 'ci-host', name: 'esx-01', type: 'Server' })
  })

  it('reads the source as downstream when the open CI is the target', () => {
    expect(relationshipCounterpart(edge, 'ci-host'))
      .toEqual({ direction: 'Downstream', ciId: 'ci-vm', name: 'vm-payroll', type: 'Virtual' })
  })
})

describe('describeRelationship', () => {
  it('always reads source first, so the words agree with WP-2.3\'s direction convention', () => {
    expect(describeRelationship(edge)).toBe('vm-payroll runs on esx-01')
  })
})

describe('relationshipGroupLabel', () => {
  // The open CI is the subject of its upstream edges and the object of its downstream ones, so one
  // heading cannot serve both without saying the opposite of what the group contains.
  it('names an upstream group by the bare verb and a downstream group by the verb acting on this CI', () => {
    expect(relationshipGroupLabel('RunsOn', 'Upstream')).toBe('Runs on')
    expect(relationshipGroupLabel('RunsOn', 'Downstream')).toBe('Runs on this CI')
    expect(relationshipGroupLabel('HostedOn', 'Upstream')).toBe('Is hosted on')
  })
})

const relationship = (over: Partial<CiRelationship> & { id: string }): CiRelationship => ({ ...edge, ...over })

describe('groupDirectRelationships', () => {
  it('gathers edges under one heading per direction and type, upstream first, named within a group', () => {
    const groups = groupDirectRelationships([
      relationship({ id: 'e1', sourceCiId: 'ci-vm', targetCiId: 'ci-sw', targetCiName: 'core-sw-01', type: 'ConnectsTo' }),
      relationship({ id: 'e2', sourceCiId: 'ci-app', sourceCiName: 'payroll-app', targetCiId: 'ci-vm', type: 'DependsOn' }),
      relationship({ id: 'e3', sourceCiId: 'ci-vm', targetCiId: 'ci-host', targetCiName: 'esx-01', type: 'RunsOn' }),
      relationship({ id: 'e4', sourceCiId: 'ci-vm', targetCiId: 'ci-host2', targetCiName: 'esx-00', type: 'RunsOn' }),
    ], 'ci-vm')

    expect(groups.map((group) => [group.label, group.edges.length]))
      .toEqual([['Connects to', 1], ['Runs on', 2], ['Depends on this CI', 1]])
    expect(groups[1].edges.map(({ counterpart }) => counterpart.name)).toEqual(['esx-00', 'esx-01'])
  })

  it('has no groups when the CI has no direct edges', () => {
    expect(groupDirectRelationships([], 'ci-vm')).toEqual([])
  })
})

const node = (id: string, name: string, depth: number): CiGraphNode =>
  ({ id, name, type: 'Server', assetTag: null, lifecycleState: 'Deployed', isActive: true, depth })

const graph = (direction: CiGraph['direction'], nodes: CiGraphNode[], edges: [string, string, string, CiRelationshipType][]): CiGraph => ({
  rootCiId: 'ci-a', direction, maxDepth: 3, maxDepthReached: false, containsCycle: false,
  nodes,
  edges: edges.map(([id, sourceCiId, targetCiId, type]) => ({ id, sourceCiId, targetCiId, type })),
})

const root = node('ci-a', 'archive-fs', 0)

describe('buildDependencyTree', () => {
  // The whole point of the tree: hop distance says core-sw-01 is two away, only the route says it is
  // reached *through* esx-01.
  it('nests each CI under the one it is reached through and labels it with that edge', () => {
    const tree = buildDependencyTree(graph('Ancestors',
      [node('ci-host', 'esx-01', 1), node('ci-sw', 'core-sw-01', 2)],
      [['e1', 'ci-a', 'ci-host', 'RunsOn'], ['e2', 'ci-host', 'ci-sw', 'ConnectsTo']]), root)

    expect(tree.node.name).toBe('archive-fs')
    expect(tree.via).toBeNull()
    const host = tree.children[0]
    expect([host.node.name, host.via]).toEqual(['esx-01', 'RunsOn'])
    expect([host.children[0].node.name, host.children[0].via]).toEqual(['core-sw-01', 'ConnectsTo'])
    expect(dependencyTreeDepth(tree)).toBe(2)
  })

  it('branches when a CI is reached through more than one route, ordering siblings by name', () => {
    const tree = buildDependencyTree(graph('Ancestors',
      [node('ci-sw', 'core-sw-01', 1), node('ci-store', 'storage-01', 1)],
      [['e1', 'ci-a', 'ci-store', 'DependsOn'], ['e2', 'ci-a', 'ci-sw', 'ConnectsTo']]), root)

    expect(tree.children.map((child) => child.node.name)).toEqual(['core-sw-01', 'storage-01'])
    expect(dependencyTreeDepth(tree)).toBe(1)
  })

  // A diamond would otherwise print the shared CI's whole subtree under each parent.
  it('draws a CI reachable two ways once and marks the second appearance as repeated', () => {
    const tree = buildDependencyTree(graph('Ancestors',
      [node('ci-sw', 'core-sw-01', 1), node('ci-store', 'storage-01', 1), node('ci-rtr', 'core-rtr', 2)],
      [['e1', 'ci-a', 'ci-sw', 'ConnectsTo'], ['e2', 'ci-a', 'ci-store', 'DependsOn'],
        ['e3', 'ci-sw', 'ci-rtr', 'ConnectsTo'], ['e4', 'ci-store', 'ci-rtr', 'ConnectsTo']]), root)

    const [viaSwitch, viaStore] = tree.children
    expect(viaSwitch.children.map((child) => [child.node.name, child.repeated])).toEqual([['core-rtr', false]])
    expect(viaStore.children.map((child) => [child.node.name, child.repeated])).toEqual([['core-rtr', true]])
    expect(viaStore.children[0].children).toEqual([])
  })

  it('walks the edges backwards for a downstream graph, because there the CI is the target', () => {
    const tree = buildDependencyTree(graph('Descendants',
      [root, node('ci-vm', 'vm-payroll', 1)],
      [['e1', 'ci-vm', 'ci-a', 'RunsOn']]), root)

    expect(tree.children.map((child) => [child.node.name, child.via])).toEqual([['vm-payroll', 'RunsOn']])
  })

  // WP-2.3 allows cycles in the data; a walk that re-entered the root would never terminate.
  it('terminates on a cycle back through the root', () => {
    const tree = buildDependencyTree(graph('Ancestors',
      [node('ci-host', 'esx-01', 1)],
      [['e1', 'ci-a', 'ci-host', 'RunsOn'], ['e2', 'ci-host', 'ci-a', 'DependsOn']]), root)

    expect(tree.children[0].children).toEqual([])
    expect(dependencyTreeDepth(tree)).toBe(1)
  })

  it('is a lone root with no levels when nothing was reached', () => {
    const tree = buildDependencyTree(graph('Ancestors', [], []), root)

    expect(tree.children).toEqual([])
    expect(dependencyTreeDepth(tree)).toBe(0)
  })
})
