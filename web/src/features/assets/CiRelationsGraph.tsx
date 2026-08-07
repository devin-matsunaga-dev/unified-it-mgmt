import { useQueries } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, GitFork, RefreshCcw } from 'lucide-react'
import { Link } from 'react-router-dom'
import { assetsApi, ciTypeLabel, type Ci, type CiGraph, type CiGraphNode } from '../../api/assets'
import { ciLifecycleTone } from './lifecycle'

const miniGraphDepth = 3

/**
 * The dependency graph around one CI, drawn as three bands: what it needs above, the CI itself, and
 * what needs it below. Nodes are grouped by hop distance, which is all the structure a card-sized
 * graph can carry legibly — the full picture stays with the traversal endpoints.
 */
export function CiRelationsGraph({ ci }: { ci: Ci }) {
  const [ancestors, impact] = useQueries({ queries: [
    { queryKey: ['cis', ci.id, 'ancestors', miniGraphDepth], queryFn: () => assetsApi.getAncestors(ci.id, miniGraphDepth) },
    { queryKey: ['cis', ci.id, 'impacted-by', miniGraphDepth], queryFn: () => assetsApi.getImpactedBy(ci.id, miniGraphDepth) },
  ] })

  const upstream = ancestors.data?.nodes ?? []
  // impacted-by includes the CI itself at depth 0; the centre band already shows it.
  const downstream = (impact.data?.nodes ?? []).filter((node) => node.id !== ci.id)
  const truncated = Boolean(ancestors.data?.maxDepthReached || impact.data?.maxDepthReached)
  const cyclic = Boolean(ancestors.data?.containsCycle || impact.data?.containsCycle)

  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
      <div><h2 className="font-semibold">Relations</h2><p className="mt-1 text-sm text-slate-500">What this CI needs, and what an outage here would take with it.</p></div>
      <span className="ml-auto text-[13px] text-slate-500">{edgeCount(ancestors.data, impact.data)} relationships</span>
    </div>

    {ancestors.isLoading || impact.isLoading
      ? <div aria-label="Loading relations" className="space-y-2 p-5">{Array.from({ length: 3 }, (_, index) => <div key={index} className="h-14 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}</div>
      : ancestors.isError || impact.isError
        ? <div role="alert" className="p-5 text-sm text-red-600">The dependency graph could not be loaded.</div>
        : upstream.length === 0 && downstream.length === 0
          ? <div className="grid place-items-center p-8 text-center"><div>
              <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><GitFork /></span>
              <p className="mt-3 text-sm text-slate-500">No relationships yet. Relate this CI to what it runs on or connects to and the graph appears here.</p>
            </div></div>
          : <div className="space-y-4 p-5">
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
  </section>
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
