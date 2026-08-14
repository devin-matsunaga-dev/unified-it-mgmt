import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, Building2, ChevronsDown, ExternalLink, RefreshCcw, ShieldAlert, Ticket, Users, Waypoints } from 'lucide-react'
import { Link } from 'react-router-dom'
import { assetsApi, ciTypeLabel, type ImpactedCi, type ImpactedTicket } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { PriorityPill } from '../tickets/ticketUi'
import { describeExposure, describeRadius, describeRing, exposureBadge, groupByDepth } from './blastRadius'
import { ciLifecycleLabel, ciLifecycleTone } from './lifecycle'

/** Hops requested first, and the server's own ceiling that "Show deeper" walks toward. */
const initialDepth = 5
const maximumDepth = 10

/**
 * What breaks if this CI dies (WP-5.2): the CIs that depend on it, the work already open on them, what
 * that is costing against the SLA, and whose it is.
 *
 * One component on two surfaces. The CI page renders it whole; the alert drawer renders `compact`,
 * which keeps the numbers and the worst few rows and links out for the rest — a 480px drawer is a peek,
 * and an operator who wants the full picture is one click from the asset page. Both read the same
 * endpoint so the two can never disagree about the size of an outage.
 */
export function BlastRadiusPanel({ ciId, compact = false, depth = initialDepth, onDeeper }: {
  ciId: string
  compact?: boolean
  depth?: number
  onDeeper?: () => void
}) {
  const impact = useQuery({
    queryKey: ['cis', ciId, 'impact', depth],
    queryFn: () => assetsApi.getImpact(ciId, depth),
    enabled: Boolean(ciId),
  })

  const body = (() => {
    if (impact.isLoading) {
      return <div aria-label="Loading blast radius" className={cn('space-y-2', compact ? '' : 'p-5')}>
        {Array.from({ length: 3 }, (_, index) => <div key={index}
          className="h-14 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}
      </div>
    }

    // "Nothing depends on this" is a claim about the estate. A failed read is a fact about the request,
    // and the two must not read the same (the WP-2.11 rule).
    if (impact.isError || !impact.data) {
      return <p role="alert" className={cn('text-sm text-red-600', compact ? '' : 'p-5')}>
        The blast radius could not be loaded.
      </p>
    }

    const { summary, cis, tickets, departments, users, containsCycle, maxDepthReached } = impact.data
    const exposure = describeExposure(summary)
    const deeper = maxDepthReached && depth < maximumDepth && onDeeper

    return <div className={cn('space-y-5', compact ? '' : 'p-5')}>
      <div className={cn('grid gap-3', compact ? 'grid-cols-2' : 'grid-cols-2 xl:grid-cols-4')}>
        <Stat icon={Waypoints} label="Affected CIs" value={summary.ciCount} tone="info" />
        <Stat icon={Ticket} label="Open tickets" value={summary.openTicketCount} tone="info" />
        <Stat icon={ShieldAlert} label="SLA breached" value={summary.breachedSlaCount}
          tone={summary.breachedSlaCount > 0 ? 'critical' : 'neutral'} />
        <Stat icon={Users} label="People affected" value={summary.affectedUserCount} tone="neutral" />
      </div>

      <p className="text-sm text-slate-600 dark:text-slate-300">
        {describeRadius(summary)}
        {exposure && <> · <span className={cn(summary.breachedSlaCount > 0 && 'font-medium text-red-600 dark:text-red-400')}>{exposure}</span></>}
      </p>

      {summary.ciCount <= 1
        ? <EmptyRadius />
        : <Section title="What is affected">
            <ul className="space-y-3">
              {groupByDepth(compact ? cis.slice(0, 6) : cis).map((ring) => <li key={ring.depth}>
                <p className="text-[13px] font-medium text-slate-500">{describeRing(ring.depth)}</p>
                <ul className="mt-1.5 space-y-1.5">
                  {ring.cis.map((ci) => <CiRow key={ci.ciId} ci={ci} root={ci.ciId === impact.data.rootCiId} />)}
                </ul>
              </li>)}
            </ul>
            {compact && cis.length > 6 && <p className="mt-2 text-[13px] text-slate-500">
              and {cis.length - 6} more.
            </p>}
          </Section>}

      {tickets.length > 0 && <Section title={`Already open on these (${summary.openTicketCount})`}>
        <ul className="space-y-2">
          {(compact ? tickets.slice(0, 4) : tickets).map((ticket) => <TicketRow key={ticket.ticketId} ticket={ticket} />)}
        </ul>
        {summary.ticketsTruncated && <p className="mt-2 text-[13px] text-slate-500">
          Showing the most exposed. Open the assets themselves for the rest.
        </p>}
      </Section>}

      {!compact && departments.length > 0 && <Section title="Departments affected">
        <ul className="flex flex-wrap gap-2">
          {departments.map((department) => <li key={department.departmentId}
            className="flex items-center gap-1.5 rounded-md bg-slate-100 px-2 py-1 text-[13px] text-slate-600 dark:bg-slate-800 dark:text-slate-300">
            <Building2 size={14} className="text-slate-400" aria-hidden />
            {department.name}
            <span className="tabular-nums text-slate-500">{department.ciCount}</span>
          </li>)}
        </ul>
        {/* Counted, never bucketed under an invented department: a blast radius that makes up an owner
            is worse than one that admits it has none. */}
        {summary.cisWithoutDepartment > 0 && <p className="mt-2 text-[13px] text-slate-500">
          {summary.cisWithoutDepartment === 1
            ? '1 affected CI records no department.'
            : `${summary.cisWithoutDepartment} affected CIs record no department.`}
        </p>}
      </Section>}

      {!compact && users.length > 0 && <Section title="Who holds the affected assets">
        <ul className="flex flex-wrap gap-2">
          {users.map((user) => <li key={user.userId}>
            <Link to={`/people/${user.userId}`}
              className="flex items-center gap-1.5 rounded-md bg-slate-100 px-2 py-1 text-[13px] text-slate-600 hover:text-blue-600 dark:bg-slate-800 dark:text-slate-300">
              <Users size={14} className="text-slate-400" aria-hidden />
              {user.name}
              <span className="tabular-nums text-slate-500">{user.ciCount}</span>
            </Link>
          </li>)}
        </ul>
      </Section>}

      <div className="flex flex-wrap items-center gap-3 border-t border-slate-200 pt-3 dark:border-slate-800">
        <span className="text-xs text-slate-500">Walked {depth} hop{depth === 1 ? '' : 's'}</span>
        {containsCycle && <span className="flex items-center gap-1.5 text-xs text-amber-700 dark:text-amber-400">
          <RefreshCcw size={13} aria-hidden />These CIs depend on each other; each is counted once.
        </span>}
        {summary.cisTruncated && <span className="flex items-center gap-1.5 text-xs text-amber-700 dark:text-amber-400">
          <AlertTriangle size={13} aria-hidden />More CIs are affected than are listed.
        </span>}
        {compact
          ? <Link to={`/assets/${ciId}`}
              className="ml-auto flex items-center gap-1.5 text-[13px] text-blue-600 hover:underline dark:text-blue-400">
              <ExternalLink size={14} />Open the full blast radius
            </Link>
          : deeper && <Button variant="secondary" className="ml-auto h-8 text-[13px]" onClick={onDeeper}>
              <ChevronsDown size={15} />Show deeper
            </Button>}
      </div>
    </div>
  })()

  if (compact) {
    return <section aria-label="Blast radius" className="space-y-3">
      <div>
        <h3 className="text-sm font-medium">Blast radius</h3>
        <p className="text-[13px] text-slate-500">What else fails if this asset does.</p>
      </div>
      {body}
    </section>
  }

  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="border-b border-slate-200 p-5 dark:border-slate-800">
      <h2 className="font-semibold">Blast radius</h2>
      <p className="mt-1 text-sm text-slate-500">
        What breaks if this fails — everything that depends on it, and what is already open on them.
      </p>
    </div>
    {body}
  </section>
}

/** One KPI tile, per DESIGN.md §6: soft-tinted icon circle, muted label, bold value. */
function Stat({ icon: Icon, label, value, tone }: {
  icon: typeof Waypoints
  label: string
  value: number
  tone: 'info' | 'critical' | 'neutral'
}) {
  return <div className="rounded-lg border border-slate-200 p-3 dark:border-slate-800">
    <span className={cn('grid size-8 place-items-center rounded-full',
      tone === 'critical' && 'bg-red-100 text-red-600 dark:bg-red-500/15 dark:text-red-400',
      tone === 'info' && 'bg-blue-50 text-blue-600 dark:bg-blue-500/15 dark:text-blue-400',
      tone === 'neutral' && 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400')}>
      <Icon size={16} aria-hidden />
    </span>
    <p className="mt-2 text-[13px] text-slate-500">{label}</p>
    <p className="text-2xl font-bold tabular-nums">{value}</p>
  </div>
}

function CiRow({ ci, root }: { ci: ImpactedCi; root: boolean }) {
  return <li className="flex flex-wrap items-center gap-2 text-sm">
    {root
      ? <><span className="font-medium">{ci.name}</span>
          <span className="rounded bg-blue-50 px-1.5 py-0.5 text-[11px] font-medium text-blue-700 dark:bg-blue-500/15 dark:text-blue-300">Current CI</span></>
      : <Link to={`/assets/${ci.ciId}`}
          className="font-medium hover:text-blue-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600">
          {ci.name}
        </Link>}
    <span className="text-[13px] text-slate-500">{ciTypeLabel(ci.type)}</span>
    <span className={cn('rounded px-1.5 py-0.5 text-[11px] font-medium', ciLifecycleTone(ci.lifecycleState))}>
      {ciLifecycleLabel(ci.lifecycleState)}
    </span>
    {ci.departmentName && <span className="text-[13px] text-slate-500">{ci.departmentName}</span>}
    {ci.openTicketCount > 0 && <span className="ml-auto flex items-center gap-1 text-[13px] text-slate-500">
      <Ticket size={13} aria-hidden />{ci.openTicketCount}
    </span>}
  </li>
}

function TicketRow({ ticket }: { ticket: ImpactedTicket }) {
  const badge = exposureBadge(ticket.sla)
  return <li>
    <Link to={`/tickets/${ticket.ticketId}`}
      className="flex flex-wrap items-center gap-2 text-sm text-blue-600 hover:underline dark:text-blue-400">
      <span className="font-mono text-xs">{ticket.number}</span>
      <span className="min-w-0 flex-1 truncate text-slate-700 dark:text-slate-200">{ticket.title}</span>
      <PriorityPill priority={ticket.priority as 'Low' | 'Medium' | 'High' | 'Critical'} />
    </Link>
    <p className="mt-0.5 flex flex-wrap items-center gap-2 text-[13px] text-slate-500">
      <span className="truncate">{ticket.ciName}</span>
      <span className={cn('rounded-md px-1.5 py-0.5 text-[11px] font-medium',
        badge.tone === 'critical' && 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-300',
        badge.tone === 'warning' && 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300',
        badge.tone === 'neutral' && 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300')}>
        {badge.label}
      </span>
    </p>
  </li>
}

/**
 * Not "No data". A CI nothing depends on has a blast radius of exactly itself, and saying so is the
 * difference between a feature that has answered and one that looks broken.
 */
function EmptyRadius() {
  return <div className="grid place-items-center rounded-lg border border-dashed border-slate-200 p-6 text-center dark:border-slate-700">
    <div>
      <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15">
        <Waypoints />
      </span>
      <p className="mt-3 text-sm text-slate-500">
        Nothing recorded depends on this CI, so losing it takes nothing else with it.
        Relate it to what runs on it and this fills in.
      </p>
    </div>
  </div>
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return <div>
    {/* Sentence case, not the mock's caps: DESIGN.md §4 rules out ALL-CAPS headers everywhere. */}
    <h3 className="text-[13px] font-semibold text-slate-600 dark:text-slate-300">{title}</h3>
    <div className="mt-2">{children}</div>
  </div>
}
