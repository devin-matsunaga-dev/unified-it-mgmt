import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Background, BackgroundVariant, Controls, MiniMap, ReactFlow, ReactFlowProvider,
  useEdgesState, useNodesState, type Edge, type Node, type NodeChange,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { Crosshair, ExternalLink, Network, Pin, PinOff, Save, TriangleAlert, Users, X } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { monitoringApi, type DeviceStatus, type DeviceStatusTile } from '../../api/monitoring'
import {
  topologyApi, unresolvedReasonLabel,
  type Topology, type TopologyMapNode, type TopologyMapSummary, type TopologyNode,
} from '../../api/topology'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { LiveIndicator } from '../monitoring/LiveIndicator'
import { statusDot, statusLabel } from '../monitoring/severity'
import { useMonitoringHub } from '../monitoring/useMonitoringHub'
import { collapseEndpoints, isEndpointGroupId, type EndpointGroup } from './endpointGroups'
import { buildAdjacency, defaultFocusHops, emphasisFor, neighbourhood, touchesCi } from './focus'
import { mergePins, resolveLayout } from './layout'
import { correlationByCi, openAlertsByCi } from './nodeEmphasis'
import {
  availableRelationshipFilters, defaultRelationshipFilter, relationshipFilterById,
} from './relationshipFilters'
import { SiteBands } from './SiteBands'
import { defaultView, topologyViews, viewById, type TopologyView } from './views'
import { TopologyNodeCard, type TopologyNodeData } from './TopologyNodeCard'

/**
 * The board reads one page of tiles and so does this — same key, same query, so the two screens share
 * a cache entry and a hub push updates whichever of them is mounted.
 */
const boardPageSize = 200
const statusBoardKey = ['monitoring', 'status-board', { search: '' }]

/** Declared once, outside the component: React Flow re-creates the whole canvas if this identity moves. */
const nodeTypes = { ci: TopologyNodeCard }

export function TopologyPage() {
  return <ReactFlowProvider><TopologyCanvas /></ReactFlowProvider>
}

function TopologyCanvas() {
  const queryClient = useQueryClient()
  const [viewId, setViewId] = useState<TopologyView['id']>(defaultView.id)
  const [selectedMapId, setSelectedMapId] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [selectedCiId, setSelectedCiId] = useState<string | null>(null)
  const [focused, setFocused] = useState(false)
  const [expandedGroups, setExpandedGroups] = useState<ReadonlySet<string>>(new Set())
  const [relationshipId, setRelationshipId] = useState(defaultRelationshipFilter.id)
  const [showSites, setShowSites] = useState(true)
  const [showMinimap, setShowMinimap] = useState(true)
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<TopologyNodeData>>([])
  const [edges, setEdges] = useEdgesState<Edge>([])

  const view = viewById(viewId)
  const types = view.types
  const topology = useQuery({
    queryKey: ['topology', { types }],
    queryFn: () => topologyApi.get(types ?? undefined),
  })
  const board = useQuery({
    queryKey: statusBoardKey,
    queryFn: () => monitoringApi.statusBoard({ pageSize: boardPageSize }),
  })
  /**
   * Open alerts, for the per-node count and for §13's correlation. Read from the same endpoint the
   * alert board uses, so a root cause the correlation engine already chose is shown rather than
   * re-derived here — nothing on this page infers a cause.
   */
  const alerts = useQuery({
    queryKey: ['monitoring', 'alerts', { status: 'Open' }],
    queryFn: () => monitoringApi.listAlerts({ status: 'Open', pageSize: boardPageSize }),
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

  const correlation = useMemo(() => correlationByCi(alerts.data?.items ?? []), [alerts.data])
  const alertCounts = useMemo(() => openAlertsByCi(alerts.data?.items ?? []), [alerts.data])

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

  /**
   * The graph as the canvas draws it: endpoints folded into one node per switch (§2). Purely derived
   * — the server's graph is never mutated, so expanding a group restores it exactly.
   */
  const display = useMemo(
    () => collapseEndpoints(graph?.nodes ?? [], graph?.edges ?? [], graph?.observedLinks ?? [], expandedGroups),
    [graph, expandedGroups])

  /** One adjacency map per display graph, so selecting a node walks a map instead of scanning edges. */
  const adjacency = useMemo(
    () => buildAdjacency(display.edges, display.observedLinks),
    [display])


  // Rebuilt when the graph, the saved layout or the filter changes — never when a status arrives,
  // which is what keeps live colouring from resetting a drag in progress.
  /**
   * Nodes and the layout. Deliberately separate from the edge effect below and deliberately NOT
   * dependent on the relationship filter: changing which relationships are drawn must not re-run
   * resolveLayout or rebuild every node, which is what §15 means by only recomputing the layout when
   * the topology structure changes.
   */
  useEffect(() => {
    if (!graph) return

    const positions = resolveLayout(display.nodes, display.edges, display.observedLinks, pins)
    const pinned = new Set(pins.map((pin) => pin.ciId))
    const groupByNodeId = new Map(display.groups.map((group) => [group.id, group]))
    setNodes(display.nodes.map((ci) => ({
      id: ci.ciId,
      type: 'ci',
      position: positions.get(ci.ciId) ?? { x: 0, y: 0 },
      data: {
        ci,
        status: null,
        deviceId: null,
        pinned: pinned.has(ci.ciId),
        emphasis: 'related',
        group: groupByNodeId.get(ci.ciId) ?? null,
        openAlerts: 0,
        correlation: null,
      },
    })))
    setDirty(false)
    // A selection from the previous graph would either highlight nothing or, in Focus Mode, hide
    // everything — so a new graph starts unselected.
    setSelectedCiId(null)
    setFocused(false)
  }, [display, graph, pins, setNodes])

  /** Edges, which are the only thing the relationship cut touches. */
  useEffect(() => {
    if (!graph) return

    const relationships = relationshipFilterById(relationshipId)
    setEdges([
      ...display.edges.filter(relationships.edge).map((edge) => ({
        id: edge.id,
        source: edge.sourceCiId,
        target: edge.targetCiId,
        // A recorded edge a scan also saw is drawn a shade heavier: same line, more confidence.
        // Kept on `data` as well, because the selection effect overwrites `style` and needs
        // something to return to when the selection is cleared.
        style: { stroke: '#cbd5e1', strokeWidth: edge.observedByDiscovery ? 2 : 1 },
        data: { baseStyle: { stroke: '#cbd5e1', strokeWidth: edge.observedByDiscovery ? 2 : 1 } },
      })),
      // Observed links the CMDB already records are folded into the edge above rather than drawn a
      // second time: one cable, one line. What is left is what a scan found and nobody wrote down.
      ...display.observedLinks
        .filter((link) => !link.matchesAssertedEdge && relationships.observed(link))
        .map((link) => ({
          id: link.id,
          source: link.sourceCiId,
          target: link.targetCiId,
          style: { stroke: '#94a3b8', strokeWidth: 1, strokeDasharray: '4 4' },
          data: { baseStyle: { stroke: '#94a3b8', strokeWidth: 1, strokeDasharray: '4 4' } },
        })),
    ])
  }, [display, graph, relationshipId, setEdges])

  // Status is written onto the existing nodes instead of rebuilding them, so a live change costs one
  // recolour rather than a re-layout.
  useEffect(() => {
    setNodes((current) => current.map((node) => {
      const live = monitored.get(node.id)
      const status = live?.status ?? null
      const deviceId = live?.deviceId ?? null
      const openAlerts = alertCounts.get(node.id) ?? 0
      const correlated = correlation.get(node.id) ?? null
      return node.data.status === status
        && node.data.deviceId === deviceId
        && node.data.openAlerts === openAlerts
        && node.data.correlation === correlated
        ? node
        : { ...node, data: { ...node.data, status, deviceId, openAlerts, correlation: correlated } }
    }))
  }, [monitored, alertCounts, correlation, setNodes])

  /** The selected CI and everything within reach of it; null when nothing is selected. */
  const neighbours = useMemo(
    () => selectedCiId === null
      ? null
      : neighbourhood(adjacency, selectedCiId, focused ? defaultFocusHops : 1),
    [adjacency, selectedCiId, focused])

  const selectedNode = useMemo(
    () => graph?.nodes.find((node) => node.ciId === selectedCiId) ?? null,
    [graph, selectedCiId])

  /**
   * Emphasis is written onto existing nodes rather than rebuilding them, for the same reason status
   * is: a click must not re-run the layout or drop a drag in progress.
   *
   * In Focus Mode a node outside the neighbourhood is hidden outright (§6); with a plain selection it
   * is only faded (§5), because what else is out there is half of what an operator is judging.
   */
  useEffect(() => {
    setNodes((current) => current.map((node) => {
      const emphasis = emphasisFor(node.id, selectedCiId, neighbours)
      const hidden = focused && selectedCiId !== null && !neighbours?.has(node.id)
      return node.data.emphasis === emphasis && (node.hidden ?? false) === hidden
        ? node
        : { ...node, hidden, data: { ...node.data, emphasis } }
    }))
  }, [selectedCiId, neighbours, focused, setNodes])

  /**
   * Edges follow the same rule. Unselected they stay thin and quiet (§5); an edge touching the
   * selection is drawn heavier and in the accent, and everything else drops to a fraction of its
   * opacity rather than disappearing.
   */
  useEffect(() => {
    setEdges((current) => current.map((edge) => {
      const related = selectedCiId !== null
        && (touchesCi(edge, selectedCiId)
          || (neighbours !== null && neighbours.has(edge.source) && neighbours.has(edge.target)))
      const direct = selectedCiId !== null && touchesCi(edge, selectedCiId)
      const hidden = focused && selectedCiId !== null
        && !(neighbours?.has(edge.source) && neighbours?.has(edge.target))

      const base = edge.data?.baseStyle as React.CSSProperties | undefined
      return {
        ...edge,
        hidden,
        animated: false,
        style: {
          ...base,
          ...(selectedCiId === null
            ? {}
            : direct
              ? { stroke: '#2563eb', strokeWidth: 2, opacity: 1 }
              : related
                ? { opacity: 0.9 }
                : { opacity: 0.15 }),
        },
        zIndex: direct ? 1 : 0,
      }
    }))
    // relationshipId is a dependency because the effect above rebuilds the edges when it changes,
    // which would otherwise drop the selection styling until the next click. It cannot loop: neither
    // effect writes anything the other depends on.
  }, [selectedCiId, neighbours, focused, relationshipId, setEdges])

  const onChange = useCallback((changes: NodeChange<Node<TopologyNodeData>>[]) => {
    onNodesChange(changes)
    if (changes.some((change) => change.type === 'position' && change.dragging === false)) {
      setDirty(true)
    }
  }, [onNodesChange])

  const onNodeClick = useCallback((_: React.MouseEvent, node: Node<TopologyNodeData>) => {
    // A group is an affordance, not a CI — clicking it opens it rather than selecting it.
    if (isEndpointGroupId(node.id)) {
      setExpandedGroups((current) => {
        const next = new Set(current)
        if (!next.delete(node.id)) next.add(node.id)
        return next
      })
      return
    }

    setSelectedCiId((current) => current === node.id ? current : node.id)
  }, [])

  /** Clicking empty canvas clears the selection and leaves Focus Mode with it. */
  const onPaneClick = useCallback(() => {
    setSelectedCiId(null)
    setFocused(false)
  }, [])

  /** Recomputed per graph, not per render: it scans every edge for every cut on offer. */
  const relationshipOptions = useMemo(
    () => availableRelationshipFilters(display.edges, display.observedLinks),
    [display])

  const visible = useMemo(
    () => new Set(nodes.map((node) => node.id).filter((id) => !isEndpointGroupId(id))),
    [nodes])
  const positionsRef = useRef(nodes)
  positionsRef.current = nodes

  const save = useMutation({
    mutationFn: async (input: { name: string; mapId: string | null }) => {
      // Endpoint groups are a drawing, not CIs. Their synthetic ids must never reach the saved map:
      // the server would be handed an id no CI has, and a later load would pin nothing.
      const onCanvas: TopologyMapNode[] = positionsRef.current
        .filter((node) => !isEndpointGroupId(node.id))
        .map((node) => ({
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

    const positions = resolveLayout(display.nodes, display.edges, display.observedLinks, [])
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
      <p className="text-sm text-slate-500">{view.description}</p>
      <div className="ml-auto"><LiveIndicator status={hub} /></div>
    </div>

    <div className="flex flex-wrap items-center gap-2">
      <div role="group" aria-label="What to show" className="flex flex-wrap gap-1">
        {topologyViews.map((option) => <button key={option.id} type="button"
          aria-pressed={viewId === option.id}
          onClick={() => setViewId(option.id)}
          className={cn('rounded-lg px-3 py-1.5 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
            viewId === option.id
              ? 'bg-blue-600 text-white'
              : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800')}>
          {option.label}
        </button>)}
      </div>

      <label className="ml-auto flex items-center gap-2 text-[13px] text-slate-500">
        Relationships
        <select
          aria-label="Relationships"
          value={relationshipId}
          onChange={(event) => setRelationshipId(event.target.value)}
          className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm text-slate-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200">
          {relationshipOptions.map((option) =>
            <option key={option.id} value={option.id}>{option.label}</option>)}
        </select>
      </label>

      <label className="flex items-center gap-2 text-[13px] text-slate-500">
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

      <label className="flex items-center gap-2 text-[13px] text-slate-600 dark:text-slate-300">
        <input type="checkbox" className="size-4 rounded border-slate-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
          checked={showSites} onChange={(event) => setShowSites(event.target.checked)} />
        Sites
      </label>
      <label className="flex items-center gap-2 text-[13px] text-slate-600 dark:text-slate-300">
        <input type="checkbox" className="size-4 rounded border-slate-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
          checked={showMinimap} onChange={(event) => setShowMinimap(event.target.checked)} />
        Minimap
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

    {selectedNode && <SelectionBar
      node={selectedNode}
      endpointGroup={display.groups.find((group) => group.parentCiId === selectedNode.ciId) ?? null}
      endpointsExpanded={expandedGroups.has(`endpoint-group:${selectedNode.ciId}`)}
      onToggleEndpoints={() => setExpandedGroups((current) => {
        const next = new Set(current)
        const id = `endpoint-group:${selectedNode.ciId}`
        if (!next.delete(id)) next.add(id)
        return next
      })}
      status={monitored.get(selectedNode.ciId)?.status ?? null}
      deviceId={monitored.get(selectedNode.ciId)?.deviceId ?? null}
      relatedCount={Math.max(0, (neighbours?.size ?? 1) - 1)}
      focused={focused}
      onToggleFocus={() => setFocused((current) => !current)}
      onClear={() => { setSelectedCiId(null); setFocused(false) }} />}

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
            onNodeClick={onNodeClick}
            onPaneClick={onPaneClick}
            nodesConnectable={false}
            edgesFocusable={false}
            fitView
            /*
             * React Flow's default minZoom is 0.5, and this estate lays out wider than twice the
             * canvas — so fitView hit the clamp and left the lower layers cut off below the frame
             * rather than fitting them. The padding keeps the outermost cards off the border.
             */
            minZoom={0.1}
            fitViewOptions={{ padding: 0.12 }}
            proOptions={{ hideAttribution: false }}
            className="rounded-xl">
            <SiteBands nodes={nodes} enabled={showSites} />
            <Background variant={BackgroundVariant.Dots} gap={16} size={1} color="#e2e8f0" />
            <Controls showInteractive={false} />
            {/*
              * §14: smaller, and collapsible, because a minimap that redraws the whole graph in
              * miniature duplicates the complexity it is meant to help escape. Node colour follows
              * status only — shape and hierarchy are the big map's job.
              */}
            {showMinimap && <MiniMap
              pannable zoomable
              nodeStrokeWidth={0}
              nodeColor={(node) => minimapColour((node as Node<TopologyNodeData>).data?.status ?? null)}
              maskColor="rgba(148, 163, 184, 0.15)"
              style={{ width: 150, height: 100 }}
              className="!bg-slate-50 dark:!bg-slate-800" />}
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

/**
 * What is selected and what can be done with it. Deliberately a bar above the canvas rather than a
 * floating overlay: the canvas is already 640px and a panel over it would cover the neighbourhood
 * the selection just revealed.
 *
 * "Impact" links to the blast radius the CI page already computes (WP-5.2) rather than recomputing
 * anything here — the topology says what is connected, impact analysis says what it costs.
 */
/** The secondary Button's classes, for anchors that must stay anchors. */
const linkButton = 'inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-4 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 focus-visible:ring-offset-2 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800'

function SelectionBar({
  node, status, deviceId, relatedCount, focused, onToggleFocus, onClear,
  endpointGroup, endpointsExpanded, onToggleEndpoints,
}: {
  node: TopologyNode
  status: DeviceStatus | null
  deviceId: string | null
  relatedCount: number
  focused: boolean
  onToggleFocus: () => void
  onClear: () => void
  /** Set when this CI is a switch whose endpoints are folded into a group. */
  endpointGroup: EndpointGroup | null
  endpointsExpanded: boolean
  onToggleEndpoints: () => void
}) {
  return <div role="region" aria-label="Selected CI"
    className="flex flex-wrap items-center gap-3 rounded-xl border border-slate-200 bg-white px-4 py-3 dark:border-slate-800 dark:bg-slate-900">
    <span className="min-w-0">
      <span className="block truncate font-semibold text-slate-900 dark:text-slate-100">{node.name}</span>
      <span className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[13px] text-slate-500">
        <span className="flex items-center gap-1.5">
          <span aria-hidden className={cn('size-2 rounded-full', status ? statusDot[status] : 'bg-slate-300 dark:bg-slate-700')} />
          {status ? statusLabel[status] : 'Not monitored'}
        </span>
        {node.address && <span>· {node.address}</span>}
        {node.siteName && <span>· {node.siteName}</span>}
        <span>· {relatedCount} {relatedCount === 1 ? 'direct relationship' : 'direct relationships'}</span>
      </span>
    </span>

    <span className="ml-auto flex flex-wrap items-center gap-2">
      {/* The only way back once a group is open: the members are CIs, so clicking one selects it. */}
      {endpointGroup && <Button variant="secondary" onClick={onToggleEndpoints} aria-pressed={endpointsExpanded}>
        <Users size={16} />
        {endpointsExpanded
          ? `Hide ${endpointGroup.memberCiIds.length} endpoints`
          : `Show ${endpointGroup.memberCiIds.length} endpoints`}
      </Button>}
      <Button variant={focused ? 'primary' : 'secondary'} onClick={onToggleFocus} aria-pressed={focused}>
        <Crosshair size={16} />{focused ? `Focused · ${defaultFocusHops} hops` : 'Focus'}
      </Button>
      {/* Styled as a secondary button rather than wrapped in one: Button renders a <button>, and a
          navigation nested in one is neither a link nor a button to a keyboard or a screen reader. */}
      <Link to={`/assets/${node.ciId}`} className={linkButton}><ExternalLink size={16} />Open CI</Link>
      {deviceId && <Link to={`/monitoring/devices/${deviceId}`} className={linkButton}>
        <ExternalLink size={16} />Device
      </Link>}
      <Button variant="ghost" onClick={onClear} aria-label="Clear selection"><X size={16} /></Button>
    </span>
  </div>
}

/** Status only, in the semantic family DESIGN.md §3 fixes. Unmonitored reads as neutral, not healthy. */
function minimapColour(status: DeviceStatus | null): string {
  if (status === 'Critical') return '#dc2626'
  if (status === 'Warning') return '#d97706'
  if (status === 'Ok') return '#16a34a'
  return '#cbd5e1'
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
