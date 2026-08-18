import type { Alert } from '../../api/monitoring'
import type { TopologyNode } from '../../api/topology'

/**
 * How much visual weight a node carries (§3). Four tiers, expressed in size, border weight, icon and
 * type — never in colour. Colour is reserved for status, which is the thing an operator is actually
 * scanning for; a hierarchy painted in hues would compete with it and DESIGN.md §3 forbids it.
 */
export type NodeTier = 'primary' | 'secondary' | 'normal' | 'low'

const primaryRoles = new Set(['Edge', 'Firewall', 'Core'])

/**
 * The tier a CI is drawn at.
 *
 * Network devices are ranked by the role the CMDB records, which is why that field was added — CiType
 * alone puts a core router and a desk-side switch on the same footing. A network device with no role
 * recorded sits at `secondary` rather than `normal`: it is still infrastructure, and guessing
 * downward would bury a core switch nobody has classified yet.
 */
export function tierFor(node: Pick<TopologyNode, 'type' | 'networkRole'>): NodeTier {
  if (node.type === 'NetworkDevice') {
    return node.networkRole !== null && primaryRoles.has(node.networkRole) ? 'primary' : 'secondary'
  }

  if (node.type === 'Server') return 'secondary'
  if (node.type === 'Hardware') return 'low'
  return 'normal'
}

/**
 * The card geometry and weight per tier. Heights vary; widths deliberately do not — the layout uses
 * one column pitch, and a wider card would overlap its neighbour. Every height stays well inside the
 * row pitch, so a taller card cannot collide with the row below.
 */
export const tierStyles: Record<NodeTier, { height: number; border: string; icon: number; name: string }> = {
  primary: { height: 84, border: 'border-2', icon: 20, name: 'text-[14px] font-semibold' },
  secondary: { height: 76, border: 'border', icon: 18, name: 'text-[13px] font-medium' },
  normal: { height: 72, border: 'border', icon: 17, name: 'text-[13px] font-medium' },
  low: { height: 64, border: 'border', icon: 16, name: 'text-[12px] font-normal' },
}

/**
 * Where a CI sits in a correlated failure (§13): the cause, or something taken out by it.
 *
 * Derived from alerts the platform already produces — WP-5.1's correlation engine files a suppressed
 * alert under the one that explains it. Nothing here infers a root cause; it reads the one the
 * engine already chose.
 */
export type CorrelationRole = 'RootCause' | 'Affected'

/**
 * CI id → its part in a correlated failure.
 *
 * A root cause is an alert that has others filed under it; an affected CI is one whose alert was
 * suppressed because a dependency was already failing. A CI that is both — its own alert suppressed
 * while still explaining others — is reported as the cause, because that is the one worth opening.
 */
export function correlationByCi(alerts: readonly Alert[]): Map<string, CorrelationRole> {
  const roles = new Map<string, CorrelationRole>()
  for (const alert of alerts) {
    if (alert.suppression === 'RootCause' && roles.get(alert.ciId) !== 'RootCause') {
      roles.set(alert.ciId, 'Affected')
    }
  }

  for (const alert of alerts) {
    if (alert.impactedCount > 0) roles.set(alert.ciId, 'RootCause')
  }

  return roles
}

/** Open alerts per CI, for the count a node shows. Cleared alerts are not a live concern. */
export function openAlertsByCi(alerts: readonly Alert[]): Map<string, number> {
  const counts = new Map<string, number>()
  for (const alert of alerts) {
    if (alert.clearedAt !== null) continue
    counts.set(alert.ciId, (counts.get(alert.ciId) ?? 0) + 1)
  }

  return counts
}
