import type { CiImpactSummary, ImpactedCi, SlaExposure } from '../../api/assets'

export type ExposureBadge = { label: string; tone: 'critical' | 'warning' | 'neutral' }

/**
 * Where one ticket stands, as one badge. Kept out of the component because the wording is the point:
 * "Breached" and "2h left" are the two things an operator triages on, and a ticket with no SLA has to
 * read as *having no clock* rather than as having plenty of time.
 */
export function exposureBadge(sla: SlaExposure | null): ExposureBadge {
  if (!sla) return { label: 'No SLA', tone: 'neutral' }
  if (sla.breached) return { label: 'SLA breached', tone: 'critical' }
  if (sla.atRisk) return { label: `At risk · ${formatWindow(sla.remainingSeconds)} left`, tone: 'warning' }
  return { label: `${formatWindow(sla.remainingSeconds)} left`, tone: 'neutral' }
}

/**
 * A duration in the coarsest unit that still says something useful. Business seconds, so "2d" means two
 * working days rather than forty-eight hours — the SLA clock stops overnight and at the weekend, and a
 * panel that rendered it as wall-clock time would promise a deadline that is not the one being counted.
 */
export function formatWindow(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds))
  const days = Math.floor(total / 86400)
  if (days > 0) return `${days}d`
  const hours = Math.floor(total / 3600)
  if (hours > 0) return `${hours}h`
  return `${Math.floor(total / 60)}m`
}

/**
 * The headline sentence. It always names the CI itself as part of the count, because it is: a blast
 * radius of one is a real answer and reads very differently from a broken panel.
 */
export function describeRadius(summary: Pick<CiImpactSummary, 'ciCount' | 'directCiCount'>): string {
  if (summary.ciCount <= 1) return 'Nothing else recorded depends on this'
  const direct = summary.directCiCount === 1 ? '1 directly' : `${summary.directCiCount} directly`
  return `${summary.ciCount - 1} other CI${summary.ciCount === 2 ? '' : 's'} affected · ${direct}`
}

/**
 * The SLA line, or null when there is nothing on the clock at all. Breaches lead: a deadline already
 * missed is the thing that changes what an operator does next.
 */
export function describeExposure(
  summary: Pick<CiImpactSummary, 'breachedSlaCount' | 'atRiskSlaCount' | 'openTicketCount'>,
): string | null {
  if (summary.openTicketCount === 0) return null
  const parts: string[] = []
  if (summary.breachedSlaCount > 0) parts.push(`${summary.breachedSlaCount} SLA breached`)
  if (summary.atRiskSlaCount > 0) parts.push(`${summary.atRiskSlaCount} at risk`)
  return parts.length === 0 ? 'No SLA breaches' : parts.join(' · ')
}

/**
 * The affected CIs in rings, nearest first. Distance is what an operator reads this list for — what
 * fails immediately, then what follows — and a flat list of forty names does not say it.
 *
 * The API already orders by depth and then by name, so this preserves that rather than re-sorting: two
 * renderings of one answer must not disagree about the order.
 */
export function groupByDepth(cis: readonly ImpactedCi[]): { depth: number; cis: ImpactedCi[] }[] {
  const rings = new Map<number, ImpactedCi[]>()
  for (const ci of cis) {
    const ring = rings.get(ci.depth)
    if (ring) ring.push(ci)
    else rings.set(ci.depth, [ci])
  }
  return [...rings.entries()].sort(([a], [b]) => a - b).map(([depth, group]) => ({ depth, cis: group }))
}

/** What a ring of the radius is called. Depth 0 is the CI the question was asked about. */
export function describeRing(depth: number): string {
  if (depth === 0) return 'This CI'
  if (depth === 1) return 'Directly dependent'
  return `${depth} hops away`
}
