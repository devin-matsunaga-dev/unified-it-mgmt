import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { AppWindow, ArrowDown, Boxes, ChevronsDown, CornerDownRight, ExternalLink, GitFork, HardDrive, MonitorSmartphone, MoreVertical, Network, Plus, RefreshCcw, Server, Trash2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi, ciTypeLabel, type Ci, type CiGraph, type CiGraphNode, type CiRelationship, type CiType } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { CiRelateDialog } from './CiRelateDialog'
import { ciLifecycleLabel, ciLifecycleTone } from './lifecycle'
import {
  buildDependencyTree, ciRelationshipVerb, dependencyTreeDepth, describeRelationship, groupDirectRelationships,
  type DependencyTreeNode,
} from './relationships'

/** Hops requested first, and the server's own ceiling that "Show deeper" walks toward. */
const initialDepth = 3
const maximumDepth = 10

/** One icon per CI type, so a node is recognisable before its label is read. */
const typeIcons: Record<CiType, typeof Server> = {
  Hardware: MonitorSmartphone,
  Server: Server,
  NetworkDevice: Network,
  Software: AppWindow,
  Virtual: HardDrive,
  Logical: Boxes,
}

/**
 * The dependency graph around one CI, in three readable sections: its own edges, the path up to what
 * it relies on, and the path down to what relies on it. Both paths are drawn as indented trees — the
 * traversal endpoints stamp each CI with a hop distance, but distance alone never says which CI a
 * far one is reached *through*, and that route is the thing an operator is actually reading for.
 *
 * The card also owns the write surface (WP-2.9). Only the CI's *direct* edges are editable, because a
 * node three hops away is joined to this one by an edge that belongs on some other CI's page.
 */
export function CiRelationsGraph({ ci }: { ci: Ci }) {
  const queryClient = useQueryClient()
  const [relating, setRelating] = useState(false)
  const [confirmingId, setConfirmingId] = useState<string | null>(null)
  const [upstreamDepth, setUpstreamDepth] = useState(initialDepth)
  const [downstreamDepth, setDownstreamDepth] = useState(initialDepth)

  const relationships = useQuery({
    queryKey: ['cis', ci.id, 'relationships'],
    queryFn: () => assetsApi.getRelationships(ci.id),
  })
  const [ancestors, impact] = useQueries({ queries: [
    { queryKey: ['cis', ci.id, 'ancestors', upstreamDepth], queryFn: () => assetsApi.getAncestors(ci.id, upstreamDepth) },
    { queryKey: ['cis', ci.id, 'impacted-by', downstreamDepth], queryFn: () => assetsApi.getImpactedBy(ci.id, downstreamDepth) },
  ] })

  const remove = useMutation({
    mutationFn: (relationshipId: string) => assetsApi.deleteRelationship(relationshipId),
    onSuccess: async () => {
      setConfirmingId(null)
      // The edge leaves both CIs' graphs, and any traversal that crossed it is now wrong.
      await queryClient.invalidateQueries({ queryKey: ['cis'] })
      toast.success('Relationship removed')
    },
  })

  const edges = [...(relationships.data?.upstream ?? []), ...(relationships.data?.downstream ?? [])]
  const groups = groupDirectRelationships(edges, ci.id)
  // A disposed CI is a frozen record of what left the estate (WP-2.2), so it gains no new edges.
  const frozen = ci.lifecycleState === 'Disposed'

  const loading = ancestors.isLoading || impact.isLoading || relationships.isLoading
  const failed = ancestors.isError || impact.isError || relationships.isError
  const upstream = ancestors.data && buildDependencyTree(ancestors.data, rootNode(ci))
  const downstream = impact.data && buildDependencyTree(impact.data, rootNode(ci))
  // An edge names both its ends but not their lifecycle, and every directly related CI is a node at
  // depth 1 of one walk or the other — so the pill comes from there rather than from a wider API.
  const lifecycleById = new Map([...(ancestors.data?.nodes ?? []), ...(impact.data?.nodes ?? [])].map((node) => [node.id, node.lifecycleState]))
  const bare = edges.length === 0 && !upstream?.children.length && !downstream?.children.length

  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
      <div>
        <h2 className="font-semibold">Relations</h2>
        <p className="mt-1 text-sm text-slate-500">{edges.length} direct relationship{edges.length === 1 ? '' : 's'}</p>
      </div>
      <Button variant="secondary" className="ml-auto h-9 shrink-0 text-[13px]" disabled={frozen} onClick={() => setRelating(true)}>
        <Plus size={16} />Relate to…
      </Button>
    </div>

    {frozen && <p className="border-b border-slate-200 px-5 py-3 text-sm text-slate-500 dark:border-slate-800">A disposed CI is a closed record — it can gain no new relationships.</p>}
    {remove.error && <p role="alert" className="border-b border-slate-200 px-5 py-3 text-sm text-red-600 dark:border-slate-800">{remove.error.message}</p>}

    {loading
      ? <div aria-label="Loading relations" className="space-y-2 p-5">{Array.from({ length: 3 }, (_, index) => <div key={index} className="h-14 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}</div>
      : failed
        ? <div role="alert" className="p-5 text-sm text-red-600">The dependency graph could not be loaded.</div>
        : bare
          ? <div className="grid place-items-center p-8 text-center"><div>
              <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><GitFork /></span>
              <p className="mt-3 text-sm text-slate-500">No relationships yet. Relate this CI to what it runs on or connects to and the graph appears here.</p>
              {!frozen && <Button className="mt-4" onClick={() => setRelating(true)}><Plus size={16} />Relate to…</Button>}
            </div></div>
          : <div className="divide-y divide-slate-200 dark:divide-slate-800">
              {groups.length > 0 && <Section title="Direct relationships">
                <div className="space-y-5">
                  {groups.map((group) => <div key={group.key}>
                    <p className="flex items-baseline gap-2 text-[13px] font-medium text-slate-500">
                      {group.label}<span className="ml-auto tabular-nums">{group.edges.length}</span>
                    </p>
                    <ul className="mt-2 space-y-2">
                      {group.edges.map(({ edge, counterpart }) => <EdgeCard key={edge.id} edge={edge}
                        ciId={counterpart.ciId} name={counterpart.name} type={counterpart.type}
                        lifecycleState={lifecycleById.get(counterpart.ciId)}
                        confirming={confirmingId === edge.id} pending={remove.isPending}
                        onConfirm={() => setConfirmingId(edge.id)} onRemove={() => remove.mutate(edge.id)} />)}
                    </ul>
                  </div>)}
                </div>
              </Section>}

              <PathSection title="Dependency path" hint="Infrastructure this CI relies on"
                empty="No upstream dependencies recorded."
                tree={upstream} graph={ancestors.data} depth={upstreamDepth}
                onDeeper={() => setUpstreamDepth((value) => Math.min(value + initialDepth, maximumDepth))} />

              <PathSection title="Downstream impact" hint="CIs that may be affected if this CI becomes unavailable"
                empty="No downstream dependencies recorded."
                tree={downstream} graph={impact.data} depth={downstreamDepth}
                onDeeper={() => setDownstreamDepth((value) => Math.min(value + initialDepth, maximumDepth))} />
            </div>}

    <CiRelateDialog ci={relating ? ci : null} existing={edges} onClose={() => setRelating(false)} />
  </section>
}

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return <div className="p-5">
    {/* Sentence case, not the mock's caps: DESIGN.md §4 rules out ALL-CAPS headers everywhere. */}
    <h3 className="text-[13px] font-semibold text-slate-600 dark:text-slate-300">{title}</h3>
    {hint && <p className="mt-0.5 text-[13px] text-slate-500">{hint}</p>}
    <div className="mt-3">{children}</div>
  </div>
}

/** One dependency tree with its depth readout, or the reason it is empty. */
function PathSection({ title, hint, empty, tree, graph, depth, onDeeper }: {
  title: string
  hint: string
  empty: string
  tree?: DependencyTreeNode
  graph?: CiGraph
  depth: number
  onDeeper: () => void
}) {
  if (!tree || !graph) return null
  const levels = dependencyTreeDepth(tree)
  const deeper = graph.maxDepthReached && depth < maximumDepth

  return <Section title={title} hint={hint}>
    {tree.children.length === 0
      ? <p className="text-sm text-slate-500">{empty}</p>
      : <>
          <ul aria-label={title} className="space-y-1"><TreeBranch branch={tree} root /></ul>
          <div className="mt-3 flex flex-wrap items-center gap-3 border-t border-slate-200 pt-3 dark:border-slate-800">
            <span className="text-xs text-slate-500">{levels} level{levels === 1 ? '' : 's'} shown</span>
            {graph.containsCycle && <span className="flex items-center gap-1.5 text-xs text-amber-700 dark:text-amber-400"><RefreshCcw size={13} />This graph contains a cycle; each CI is drawn once.</span>}
            {deeper && <Button variant="secondary" className="ml-auto h-8 text-[13px]" onClick={onDeeper}><ChevronsDown size={15} />Show deeper</Button>}
          </div>
        </>}
  </Section>
}

/**
 * One CI in the path and everything reached through it. The verb sits on the connector above a node
 * rather than beside it, so the label always names the edge the reader has just travelled down.
 */
function TreeBranch({ branch, root = false }: { branch: DependencyTreeNode; root?: boolean }) {
  const Icon = typeIcons[branch.node.type] ?? Boxes
  return <li>
    {branch.via && <p className="flex items-center gap-1.5 py-0.5 pl-1 text-xs text-slate-500">
      <ArrowDown size={13} className="shrink-0 text-slate-400" aria-hidden />{ciRelationshipVerb(branch.via)}
    </p>}
    <div className="flex flex-wrap items-center gap-2 text-sm">
      <Icon size={16} className="shrink-0 text-slate-400" aria-hidden />
      {root
        ? <><span className="font-medium">{branch.node.name}</span>
            <span className="rounded bg-blue-50 px-1.5 py-0.5 text-[11px] font-medium text-blue-700 dark:bg-blue-500/15 dark:text-blue-300">Current CI</span></>
        : <Link to={`/assets/${branch.node.id}`} className="font-medium hover:text-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600">{branch.node.name}</Link>}
      {!root && <>
        <span className="text-[13px] text-slate-500">{ciTypeLabel(branch.node.type)}</span>
        <span className={`rounded px-1.5 py-0.5 text-[11px] font-medium ${ciLifecycleTone(branch.node.lifecycleState)}`}>{ciLifecycleLabel(branch.node.lifecycleState)}</span>
      </>}
      {branch.repeated && <span className="flex items-center gap-1 text-xs text-slate-400"><CornerDownRight size={13} aria-hidden />already shown above</span>}
    </div>
    {branch.children.length > 0 && <ul className="ml-2 mt-1 space-y-1 border-l border-slate-200 pl-4 dark:border-slate-800">
      {branch.children.map((child) => <TreeBranch key={child.key} branch={child} />)}
    </ul>}
  </li>
}

/** One direct edge as a card: the CI at the far end, what the edge records, and the actions on it. */
function EdgeCard({ edge, ciId, name, type, lifecycleState, confirming, pending, onConfirm, onRemove }: {
  edge: CiRelationship
  ciId: string
  name: string
  type: CiType
  lifecycleState?: string
  confirming: boolean
  pending: boolean
  onConfirm: () => void
  onRemove: () => void
}) {
  const Icon = typeIcons[type] ?? Boxes

  return <li className="flex flex-wrap items-start gap-3 rounded-lg border border-slate-200 p-3 dark:border-slate-700">
    <span className="grid size-8 shrink-0 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400"><Icon size={16} /></span>
    <div className="min-w-0 flex-1">
      <Link to={`/assets/${ciId}`} className="text-sm font-medium hover:text-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600">{name}</Link>
      {edge.description && <p className="mt-0.5 text-[13px] text-slate-500">{edge.description}</p>}
    </div>
    <div className="flex shrink-0 items-center gap-2 text-right">
      <div>
        <p className="text-[13px] text-slate-500">{ciTypeLabel(type)}</p>
        {lifecycleState && <p className={`mt-1 inline-block rounded px-1.5 py-0.5 text-[11px] font-medium ${ciLifecycleTone(lifecycleState)}`}>{ciLifecycleLabel(lifecycleState)}</p>}
      </div>
      <EdgeMenu edge={edge} ciId={ciId} confirming={confirming} pending={pending} onConfirm={onConfirm} onRemove={onRemove} />
    </div>
  </li>
}

/**
 * The per-edge action menu. Removal keeps WP-2.9's two-step confirm — the menu opening is not the
 * confirmation, so the destructive click is still made deliberately against a named relationship.
 */
function EdgeMenu({ edge, ciId, confirming, pending, onConfirm, onRemove }: {
  edge: CiRelationship
  ciId: string
  confirming: boolean
  pending: boolean
  onConfirm: () => void
  onRemove: () => void
}) {
  const [open, setOpen] = useState(false)
  const container = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const close = (event: MouseEvent) => { if (!container.current?.contains(event.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [open])

  return <div ref={container} className="relative">
    <Button variant="ghost" className="size-8 p-0" aria-expanded={open}
      aria-label={`Actions for ${describeRelationship(edge)}`} onClick={() => setOpen((value) => !value)}><MoreVertical size={16} /></Button>
    {open && <div className="absolute right-0 top-9 z-10 w-52 rounded-lg border border-slate-200 bg-white p-1 text-left shadow-sm dark:border-slate-700 dark:bg-slate-800">
      <Link to={`/assets/${ciId}`} className="flex h-9 items-center gap-2 rounded px-2 text-sm text-slate-700 hover:bg-slate-50 dark:text-slate-200 dark:hover:bg-slate-700">
        <ExternalLink size={15} />Open asset
      </Link>
      {confirming
        ? <button type="button" className="flex h-9 w-full items-center gap-2 rounded px-2 text-sm font-medium text-red-600 hover:bg-red-50 dark:hover:bg-red-500/10"
            disabled={pending} onClick={onRemove}>
            <Trash2 size={15} />Confirm remove
          </button>
        : <button type="button" className="flex h-9 w-full items-center gap-2 rounded px-2 text-sm text-red-600 hover:bg-red-50 dark:hover:bg-red-500/10"
            disabled={pending} aria-label={`Remove relationship: ${describeRelationship(edge)}`} onClick={onConfirm}>
            <Trash2 size={15} />Remove relationship
          </button>}
    </div>}
  </div>
}

/** The open CI as a graph node, so a tree can be rooted at it whether or not the walk returned it. */
function rootNode(ci: Ci): CiGraphNode {
  return { id: ci.id, type: ci.type, name: ci.name, assetTag: ci.assetTag, lifecycleState: ci.lifecycleState, isActive: ci.isActive, depth: 0 }
}
