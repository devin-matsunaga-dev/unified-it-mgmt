import type { Alert, AlertSuppression } from '../../api/monitoring'

/**
 * How an alert's suppression is worded on a board. Kept out of the components because the wording is
 * the whole point: an operator who reads "Suppressed" without a reason learns only that the platform
 * decided not to tell them something.
 */
export function suppressionLabel(suppression: AlertSuppression): string | null {
  switch (suppression) {
    case 'None':
      return null
    // Not "Suppressed: RootCause". The other two name a state the operator put the device in; this one
    // names a judgement the platform made, and it has to read as a sentence to be arguable with.
    case 'RootCause':
      return 'Suppressed under its root cause'
    default:
      return `Suppressed: ${suppression}`
  }
}

export type CorrelationBadge = { label: string; tone: 'info' | 'neutral' }

/**
 * The WP-5.1 grouping, as one badge. Three states and they are mutually exclusive in practice, because
 * only a root cause is ever named as one — a middle link in a chain is filed under the far end, so it
 * is impacted and impacts nothing.
 *
 * Being a cause is checked first regardless: it is the row somebody has to act on, and if the two ever
 * did coincide that is the more useful thing to say.
 */
export function correlationBadge(
  alert: Pick<Alert, 'suppression' | 'rootCauseAlertId' | 'impactedCount'>,
): CorrelationBadge | null {
  if (alert.impactedCount > 0) {
    return {
      label: `Root cause · ${alert.impactedCount} impacted`,
      tone: 'info',
    }
  }

  if (alert.suppression === 'RootCause') {
    return { label: 'Suppressed under its root cause', tone: 'neutral' }
  }

  // Filed under a cause but not suppressed by it: this alert had already been published when the cause
  // appeared, so it has a ticket of its own. Saying "related" rather than "suppressed" is the
  // difference between a ticket somebody should expect to find and one they should not.
  if (alert.rootCauseAlertId !== null) {
    return { label: 'Related to a root cause', tone: 'neutral' }
  }

  return null
}
