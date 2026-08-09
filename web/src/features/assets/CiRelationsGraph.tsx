import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, GitFork, Plus, RefreshCcw, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi, ciTypeLabel, type Ci, type CiGraph, type CiGraphNode, type CiRelationship } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { CiRelateDialog } from './CiRelateDialog'
import { ciLifecycleTone } from './lifecycle'
import { ciRelationshipVerb, describeRelationship, relationshipCounterpart } from './relationships'

const miniGraphDepth = 3

/**
 * The dependency graph around one CI, drawn as three bands: what it needs above, the CI itself, and
 * what needs it below. Nodes are grouped by hop distance, which is all the structure a card-sized
 * graph can carry legibly — the full picture stays with the traversal endpoints.
 *
 * The card also owns the write surface (WP-2.9). Only the CI's *direct* edges are editable, because a
 * node three hops away is joined to this one by an edge that belongs on some other CI's page.
 */
export function CiRelationsGraph({ ci }: { ci: Ci }) {
  const queryClient = useQueryClient()
  const [relating, setRelating] = useState(false)
  const [confirmingId, setConfirmingId] = useState<string | null>(null)

  const relationships = useQuery({
    queryKey: ['cis', ci.id, 'relationships'],
    queryFn: () => assetsApi.getRelationships(ci.id),
  })
  const [ancestors, impact] = useQueries({ queries: [
    { queryKey: ['cis', ci.id, 'ancestors', miniGraphDepth], queryFn: () => assetsApi.getAncestors(ci.id, miniGraphDepth) },
    { queryKey: ['cis', ci.id, 'impacted-by', miniGraphDepth], queryFn: () => assetsApi.getImpactedBy(ci.id, miniGraphDepth) },
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

  const upstream = ancestors.data?.nodes ?? []
  // impacted-by includes the CI itself at depth 0; the centre band already shows it.
  const downstream = (impact.data?.nodes ?? []).filter((node) => node.id !== ci.id)
  const truncated = Boolean(ancestors.data?.maxDepthReached || impact.data?.maxDepthReached)
  const cyclic = Boolean(ancestors.data?.containsCycle || impact.data?.containsCycle)

  const edges = [...(relationships.data?.upstream ?? []), ...(relationships.data?.downstream ?? [])]
  // A disposed CI is a frozen record of what left the estate (WP-2.2), so it gains no new edges.
  const frozen = ci.lifecycleState === 'Disposed'

  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
      <div><h2 className="font-semibold">Relations</h2><p className="mt-1 text-sm text-slate-500">What this CI needs, and what an outage here would take with it.</p></div>
      <span className="ml-auto text-[13px] text-slate-500">{edgeCount(ancestors.data, impact.data)} relationships</span>
      <Button variant="secondary" className="h-9 shrink-0 text-[13px]" disabled={frozen} onClick={() => setRelating(true)}>
        <Plus size={16} />Relate to…
      </Button>
    </div>

    {frozen && <p className="border-b border-slate-200 px-5 py-3 text-sm text-slate-500 dark:border-slate-800">A disposed CI is a closed record — it can gain no new relationships.</p>}

    {edges.length > 0 && <ul className="divide-y divide-slate-200 border-b border-slate-200 dark:divide-slate-800 dark:border-slate-800">
      {edges.map((edge) => <EdgeRow key={edge.id} ci={ci} edge={edge}
        confirming={confirmingId === edge.id} pending={remove.isPending}
        onConfirm={() => setConfirmingId(edge.id)} onRemove={() => remove.mutate(edge.id)} />)}
    </ul>}

    {remove.error && <p role="alert" className="border-b border-slate-200 px-5 py-3 text-sm text-red-600 dark:border-slate-800">{remove.error.message}</p>}

    {ancestors.isLoading || impact.isLoading
      ? <div aria-label="Loading relations" className="space-y-2 p-5">{Array.from({ length: 3 }, (_, index) => <div key={index} className="h-14 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}</div>
      : ancestors.isError || impact.isError
        ? <div role="alert" className="p-5 text-sm text-red-600">The dependency graph could not be loaded.</div>
        : upstream.length === 0 && downstream.length === 0
          ? <div className="grid place-items-center p-8 text-center"><div>
              <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><GitFork /></span>
              <p className="mt-3 text-sm text-slate-500">No relationships yet. Relate this CI to what it runs on or connects to and the graph appears here.</p>
              {!frozen && <Button className="mt-4" onClick={() => setRelating(true)}><Plus size={16} />Relate to…</Button>}
            </div></div>
          : <div aria-label="Dependency graph" className="space-y-4 p-5">
              <Band title="Depends on" hint="Upstream — a failure here breaks this CI" icon={ArrowUp} nodes={upstream} />
              <div className="flex items-center gap-3">
                <span className="h-px flex-1 bg-slate-200 dark:bg-slate-800" />
                <span className="rounded-lg border-2 border-blue-600 bg-blue-50 px-3 py-2 text-sm font-semibold text-blue-700 dark:bg-blue-500/15 dark:text-blue-300">{ci.name}</span>
                <span className="h-px flex-1 bg-slate-200 dark:bg-slate-800" />
              </div>
              <Band title="Impacted by an outage" hint="Downstream — these break when this CI does" icon={ArrowDown} nodes={downstream} />
              {(truncated || cyclic) && <p className="flex items-center gap-2 border-t border-slate-200 pt-3 text-xs text-amber-700 dark:border-slate-800 dark:text-amber-400">
                <RefreshCcw size={14} />
                {cyclic && <span>This graph contains a cycle; the walk visits each CI once.</span>}
                {truncated && <span>Stops at {miniGraphDepth} hops — deeper relations are not shown.</span>}
              </p>}
            </div>}

    <CiRelateDialog ci={relating ? ci : null} existing={edges} onClose={() => setRelating(false)} />
  </section>
}

/**
 * One direct edge, read as a sentence in the fixed source → target direction so the words never
 * disagree with the arrow, with the far end linked because that is the CI the reader wants next.
 */
function EdgeRow({ ci, edge, confirming, pending, onConfirm, onRemove }: {
  ci: Ci
  edge: CiRelationship
  confirming: boolean
  pending: boolean
  onConfirm: () => void
  onRemove: () => void
}) {
  const other = relationshipCounterpart(edge, ci.id)
  const Icon = other.direction === 'Upstream' ? ArrowUp : ArrowDown

  return <li className="flex flex-wrap items-center gap-3 px-5 py-3">
    <Icon size={15} className="shrink-0 text-slate-400" aria-hidden />
    <div className="min-w-0 flex-1">
      <p className="text-sm">
        {other.direction === 'Upstream'
          ? <>This CI <span className="text-slate-500">{ciRelationshipVerb(edge.type)}</span> <Link to={`/assets/${other.ciId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{other.name}</Link></>
          : <><Link to={`/assets/${other.ciId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{other.name}</Link> <span className="text-slate-500">{ciRelationshipVerb(edge.type)}</span> this CI</>}
        <span className="ml-2 text-[13px] text-slate-500">{ciTypeLabel(other.type)}</span>
      </p>
      {edge.description && <p className="mt-0.5 text-[13px] text-slate-500">{edge.description}</p>}
    </div>
    {confirming
      ? <Button variant="secondary" className="h-9 text-[13px] text-red-600" disabled={pending} onClick={onRemove}>Confirm remove</Button>
      : <Button variant="ghost" className="h-9 px-2 text-[13px] text-red-600" disabled={pending}
          aria-label={`Remove relationship: ${describeRelationship(edge)}`} onClick={onConfirm}><Trash2 size={16} />Remove</Button>}
  </li>
}

function Band({ title, hint, icon: Icon, nodes }: { title: string; hint: string; icon: typeof ArrowUp; nodes: CiGraphNode[] }) {
  const depths = [...new Set(nodes.map((node) => node.depth))].sort((left, right) => left - right)
  return <div>
    <p className="flex items-center gap-2 text-[13px] font-medium text-slate-500"><Icon size={15} />{title}</p>
    {nodes.length === 0
      ? <p className="mt-2 text-sm text-slate-400">Nothing recorded. <span className="sr-only">{hint}</span></p>
      : <div className="mt-2 space-y-2">
          {depths.map((depth) => <div key={depth} className="flex flex-wrap items-center gap-2">
            <span className="w-14 shrink-0 text-xs tabular-nums text-slate-400">{depth} hop{depth === 1 ? '' : 's'}</span>
            {nodes.filter((node) => node.depth === depth).map((node) => <NodeCard key={node.id} node={node} />)}
          </div>)}
        </div>}
  </div>
}

function NodeCard({ node }: { node: CiGraphNode }) {
  return <Link to={`/assets/${node.id}`} className="flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm hover:border-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:border-slate-700 dark:bg-slate-900">
    <span className="font-medium">{node.name}</span>
    <span className="text-xs text-slate-500">{ciTypeLabel(node.type)}</span>
    <span className={`rounded px-1.5 py-0.5 text-[11px] font-medium ${ciLifecycleTone(node.lifecycleState)}`}>{node.lifecycleState}</span>
  </Link>
}

function edgeCount(ancestors?: CiGraph, impact?: CiGraph) {
  const ids = new Set([...(ancestors?.edges ?? []), ...(impact?.edges ?? [])].map((edge) => edge.id))
  return ids.size
}
