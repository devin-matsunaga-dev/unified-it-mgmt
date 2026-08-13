import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Background, BackgroundVariant, Controls, MiniMap, ReactFlow, ReactFlowProvider,
  useEdgesState, useNodesState, type Edge, type Node, type NodeChange,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { Network, Pin, PinOff, Save, TriangleAlert } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import type { CiType } from '../../api/assets'
import { ApiError } from '../../api/client'
import { monitoringApi, type DeviceStatus, type DeviceStatusTile } from '../../api/monitoring'
import {
  topologyApi, unresolvedReasonLabel,
  type Topology, type TopologyMapNode, type TopologyMapSummary,
} from '../../api/topology'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { LiveIndicator } from '../monitoring/LiveIndicator'
import { useMonitoringHub } from '../monitoring/useMonitoringHub'
import { mergePins, resolveLayout } from './layout'
import { TopologyNodeCard, type TopologyNodeData } from './TopologyNodeCard'

/**
 * The board reads one page of tiles and so does this — same key, same query, so the two screens share
 * a cache entry and a hub push updates whichever of them is mounted.
 */
const boardPageSize = 200
const statusBoardKey = ['monitoring', 'status-board', { search: '' }]

/** Declared once, outside the component: React Flow re-creates the whole canvas if this identity moves. */
const nodeTypes = { ci: TopologyNodeCard }

const typeFilters: { label: string; types: CiType[] | null }[] = [
  { label: 'Everything', types: null },
  { label: 'Network', types: ['NetworkDevice'] },
  { label: 'Network and servers', types: ['NetworkDevice', 'Server', 'Virtual'] },
]

export function TopologyPage() {
  return <ReactFlowProvider><TopologyCanvas /></ReactFlowProvider>
}

function TopologyCanvas() {
  const queryClient = useQueryClient()
  const [filter, setFilter] = useState(0)
  const [selectedMapId, setSelectedMapId] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<TopologyNodeData>>([])
  const [edges, setEdges] = useEdgesState<Edge>([])

  const types = typeFilters[filter].types
  const topology = useQuery({
    queryKey: ['topology', { types }],
    queryFn: () => topologyApi.get(types ?? undefined),
  })
  const board = useQuery({
    queryKey: statusBoardKey,
    queryFn: () => monitoringApi.statusBoard({ pageSize: boardPageSize }),
  })
  const maps = useQuery({ queryKey: ['topology-maps'], queryFn: () => topologyApi.listMaps() })
  const savedMap = useQuery({
    queryKey: ['topology-maps', selectedMapId],
    queryFn: () => topologyApi.getMap(selectedMapId!),
    enabled: selectedMapId !== null,
  })

  /**
   * A hub push replaces the one tile it names. The map then recolours the node for that CI and
   * nothing else — the canvas is not rebuilt, so a device going Critical does not move anything an
   * operator has just arranged.
   */
  const onDeviceStatusChanged = useCallback((tile: DeviceStatusTile) => {
    queryClient.setQueriesData<Awaited<ReturnType<typeof monitoringApi.statusBoard>>>(
      { queryKey: ['monitoring', 'status-board'] },
      (current) => current && ({
        ...current,
        items: current.items.map((item) => item.deviceId === tile.deviceId ? tile : item),
      }),
    )
  }, [queryClient])

  const onResync = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['monitoring'] })
  }, [queryClient])

  const events = useMemo(() => ({ onDeviceStatusChanged, onResync }), [onDeviceStatusChanged, onResync])
  const hub = useMonitoringHub(events)

  /** CI id → what monitoring says about it. A CI with no monitored device is absent, not "Ok". */
  const monitored = useMemo(() => {
    const byCi = new Map<string, { status: DeviceStatus; deviceId: string }>()
    for (const tile of board.data?.items ?? []) {
      byCi.set(tile.ciId, { status: tile.status, deviceId: tile.deviceId })
    }

    return byCi
  }, [board.data])

  const pins = useMemo(() => savedMap.data?.nodes ?? [], [savedMap.data])
  const graph = topology.data

  // Rebuilt when the graph, the saved layout or the filter changes — never when a status arrives,
  // which is what keeps live colouring from resetting a drag in progress.
  useEffect(() => {
    if (!graph) return

    const positions = resolveLayout(graph.nodes, graph.edges, graph.observedLinks, pins)
    const pinned = new Set(pins.map((pin) => pin.ciId))
    setNodes(graph.nodes.map((ci) => ({
      id: ci.ciId,
      type: 'ci',
      position: positions.get(ci.ciId) ?? { x: 0, y: 0 },
      data: { ci, status: null, deviceId: null, pinned: pinned.has(ci.ciId) } as TopologyNodeData,
    })))

    setEdges([
      ...graph.edges.map((edge) => ({
        id: edge.id,
        source: edge.sourceCiId,
        target: edge.targetCiId,
        // A recorded edge a scan also saw is drawn a shade heavier: same line, more confidence.
        style: { stroke: '#cbd5e1', strokeWidth: edge.observedByDiscovery ? 2.5 : 1.5 },
      })),
      // Observed links the CMDB already records are folded into the edge above rather than drawn a
      // second time: one cable, one line. What is left is what a scan found and nobody wrote down.
      ...graph.observedLinks.filter((link) => !link.matchesAssertedEdge).map((link) => ({
        id: link.id,
        source: link.sourceCiId,
        target: link.targetCiId,
        style: { stroke: '#94a3b8', strokeWidth: 1.5, strokeDasharray: '4 4' },
      })),
    ])
    setDirty(false)
  }, [graph, pins, setEdges, setNodes])

  // Status is written onto the existing nodes instead of rebuilding them, so a live change costs one
  // recolour rather than a re-layout.
  useEffect(() => {
    setNodes((current) => current.map((node) => {
      const live = monitored.get(node.id)
      const status = live?.status ?? null
      const deviceId = live?.deviceId ?? null
      return node.data.status === status && node.data.deviceId === deviceId
        ? node
        : { ...node, data: { ...node.data, status, deviceId } }
    }))
  }, [monitored, setNodes])

  const onChange = useCallback((changes: NodeChange<Node<TopologyNodeData>>[]) => {
    onNodesChange(changes)
    if (changes.some((change) => change.type === 'position' && change.dragging === false)) {
      setDirty(true)
    }
  }, [onNodesChange])

  const visible = useMemo(() => new Set(nodes.map((node) => node.id)), [nodes])
  const positionsRef = useRef(nodes)
  positionsRef.current = nodes

  const save = useMutation({
    mutationFn: async (input: { name: string; mapId: string | null }) => {
      const onCanvas: TopologyMapNode[] = positionsRef.current.map((node) => ({
        ciId: node.id,
        x: Math.round(node.position.x),
        y: Math.round(node.position.y),
      }))
      const body = {
        name: input.name,
        nodes: mergePins(onCanvas, savedMap.data?.nodes ?? [], visible),
      }
      return input.mapId
        ? topologyApi.updateMap(input.mapId, body)
        : topologyApi.createMap(body)
    },
    onSuccess: async (saved) => {
      setSelectedMapId(saved.id)
      setDirty(false)
      await queryClient.invalidateQueries({ queryKey: ['topology-maps'] })
      toast.success(`Layout saved to "${saved.name}"`)
    },
    onError: (error: unknown) => {
      toast.error(error instanceof ApiError ? error.message : 'The layout could not be saved.')
    },
  })

  const onSave = () => {
    if (selectedMapId) {
      save.mutate({ name: savedMap.data?.name ?? 'Topology', mapId: selectedMapId })
      return
    }

    const name = window.prompt('Name this map', 'Estate topology')?.trim()
    if (name) save.mutate({ name, mapId: null })
  }

  const onReset = () => {
    if (!graph) return

    const positions = resolveLayout(graph.nodes, graph.edges, graph.observedLinks, [])
    setNodes((current) => current.map((node) => ({
      ...node,
      position: positions.get(node.id) ?? node.position,
      data: { ...node.data, pinned: false },
    })))
    setDirty(true)
  }

  const isLoading = topology.isLoading || (selectedMapId !== null && savedMap.isLoading)

  return <div className="space-y-4">
    <div className="flex flex-wrap items-center gap-3">
      <h1 className="text-[28px] font-bold text-slate-900 dark:text-slate-100">Topology</h1>
      <p className="text-sm text-slate-500">
        Relationships the CMDB records, and the links discovery saw.
      </p>
      <div className="ml-auto"><LiveIndicator status={hub} /></div>
    </div>

    <div className="flex flex-wrap items-center gap-2">
      <div role="group" aria-label="What to show" className="flex gap-1">
        {typeFilters.map((option, index) => <button key={option.label} type="button"
          aria-pressed={filter === index}
          onClick={() => setFilter(index)}
          className={cn('rounded-lg px-3 py-1.5 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
            filter === index
              ? 'bg-blue-600 text-white'
              : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800')}>
          {option.label}
        </button>)}
      </div>

      <label className="ml-auto flex items-center gap-2 text-[13px] text-slate-500">
        Saved map
        <select
          aria-label="Saved map"
          value={selectedMapId ?? ''}
          onChange={(event) => setSelectedMapId(event.target.value || null)}
          className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm text-slate-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200">
          <option value="">Auto-layout</option>
          {(maps.data ?? []).map((map: TopologyMapSummary) =>
            <option key={map.id} value={map.id}>{map.name}</option>)}
        </select>
      </label>

      <Button variant="secondary" onClick={onReset} disabled={!graph}>
        <PinOff size={16} /> Reset to auto-layout
      </Button>
      <Button onClick={onSave} disabled={!dirty || save.isPending}>
        <Save size={16} /> {selectedMapId ? 'Save layout' : 'Save as new map'}
      </Button>
    </div>

    {graph?.nodeLimitReached && <p role="status"
      className="flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-[13px] text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
      <TriangleAlert size={16} />
      Showing the {graph.nodeLimit} most connected CIs. This is part of the estate, not all of it.
    </p>}

    <div className="h-[640px] rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      {isLoading
        ? <div className="h-full animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
        : graph && graph.nodes.length === 0
          ? <EmptyState />
          : <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            onNodesChange={onChange}
            nodesConnectable={false}
            edgesFocusable={false}
            fitView
            proOptions={{ hideAttribution: false }}
            className="rounded-xl">
            <Background variant={BackgroundVariant.Dots} gap={16} size={1} color="#e2e8f0" />
            <Controls showInteractive={false} />
            <MiniMap pannable zoomable className="!bg-slate-50 dark:!bg-slate-800" />
          </ReactFlow>}
    </div>

    <div className="flex flex-wrap items-center gap-4 text-[12px] text-slate-500">
      <span className="flex items-center gap-1.5"><Line /> Recorded relationship</span>
      <span className="flex items-center gap-1.5"><Line dashed /> Seen by discovery, not recorded</span>
      <span className="flex items-center gap-1.5"><Pin size={12} className="text-blue-600" /> Pinned by hand</span>
      {dirty && <span className="text-blue-600">Layout changed — save it to keep it.</span>}
    </div>

    {(graph?.unresolvedNeighbours.length ?? 0) > 0 && <UnresolvedNeighbours topology={graph!} />}
  </div>
}

function Line({ dashed }: { dashed?: boolean }) {
  return <span aria-hidden className={cn('inline-block h-0 w-6 border-t-2', dashed
    ? 'border-dashed border-slate-400'
    : 'border-slate-300')} />
}

function EmptyState() {
  return <div className="flex h-full flex-col items-center justify-center gap-3 text-center">
    <span className="grid size-12 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800">
      <Network size={22} />
    </span>
    <p className="text-sm text-slate-600 dark:text-slate-300">Nothing is related yet, so there is no shape to draw.</p>
    <p className="max-w-md text-[13px] text-slate-500">
      A map is built from the relationships between CIs. Relate two on a CI page, or let a scan report
      its neighbours, and they appear here.
    </p>
    <Link to="/assets" className="text-[13px] font-medium text-blue-600 hover:underline">Go to assets</Link>
  </div>
}

/**
 * Neighbours a device reported that no CI answers to. Counted and named rather than drawn: a node on
 * this map is a CI, and the place an unknown device becomes one is the review queue.
 */
function UnresolvedNeighbours({ topology }: { topology: Topology }) {
  return <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-center gap-2">
      <h2 className="text-base font-semibold text-slate-900 dark:text-slate-100">
        Neighbours with no CI ({topology.unresolvedNeighbours.length})
      </h2>
      <Link to="/assets/discovery" className="ml-auto text-[13px] font-medium text-blue-600 hover:underline">
        Review queue
      </Link>
    </div>
    <p className="mt-1 text-[13px] text-slate-500">
      Devices reported these as being on the other end of a cable. Nothing in the CMDB matches them, so
      they are not on the map.
    </p>
    <ul className="mt-3 divide-y divide-slate-200 dark:divide-slate-800">
      {topology.unresolvedNeighbours.slice(0, 25).map((neighbour, index) =>
        <li key={`${neighbour.reportedByCiId}-${neighbour.localPort}-${index}`}
          className="flex flex-wrap items-baseline gap-x-2 py-2 text-[13px]">
          <span className="font-medium text-slate-700 dark:text-slate-200">
            {neighbour.remoteSystemName ?? neighbour.remoteAddress ?? 'Unnamed device'}
          </span>
          <span className="text-slate-500">
            reported by {neighbour.reportedByCiName}
            {neighbour.localPort ? ` on ${neighbour.localPort}` : ''}
            {' · '}{unresolvedReasonLabel(neighbour.reason)}
          </span>
          <span className="ml-auto text-[11px] uppercase text-slate-400">{neighbour.protocol}</span>
        </li>)}
    </ul>
  </section>
}
