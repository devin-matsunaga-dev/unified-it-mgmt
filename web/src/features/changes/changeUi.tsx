import type { ChangeStatus } from '../../api/changes'
import type { MaintenanceWindowStatus } from '../../api/monitoring'
import { cn } from '../../lib/utils'

/** The lifecycle in the order somebody reads it, which is what the filter offers. */
export const changeStatuses: ChangeStatus[] = [
  'Draft',
  'Submitted',
  'Approved',
  'Rejected',
  'Cancelled',
]

/**
 * What each button says. "Submitted" and "Draft" are states, not acts, and a button labelled with a
 * state reads as a description of where you are rather than of what pressing it does.
 */
export const changeActionLabel = (target: ChangeStatus) => ({
  Draft: 'Return to draft',
  Submitted: 'Submit for approval',
  Approved: 'Approve',
  Rejected: 'Reject',
  Cancelled: 'Cancel change',
} as Record<ChangeStatus, string>)[target]

/** DESIGN §3's semantic families: waiting is blue, agreed is green, refused is red, dropped is neutral. */
export function ChangeStatusPill({ status }: { status: ChangeStatus }) {
  return <span className={cn(
    'inline-flex rounded-md px-2 py-0.5 text-xs font-medium',
    status === 'Draft' && 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
    status === 'Submitted' && 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300',
    status === 'Approved' && 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300',
    status === 'Rejected' && 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300',
    status === 'Cancelled' && 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
  )}>{status}</span>
}

/** In progress is the one that matters on a board: it means alerts are being withheld right now. */
export function WindowStatusPill({ status }: { status: MaintenanceWindowStatus }) {
  return <span className={cn(
    'inline-flex rounded-md px-2 py-0.5 text-xs font-medium',
    status === 'Scheduled' && 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300',
    status === 'InProgress' && 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300',
    status === 'Ended' && 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
  )}>{status === 'InProgress' ? 'Muting now' : status}</span>
}

/**
 * What a change covers, in words. Says the dependents out loud because agreeing to touch one switch
 * while silencing eleven hosts is the thing a reviewer most needs to notice.
 */
export function coverageSummary(ciCount: number, dependentCount: number) {
  const named = ciCount - dependentCount
  const items = `${named} item${named === 1 ? '' : 's'}`
  return dependentCount === 0
    ? items
    : `${items} + ${dependentCount} dependent${dependentCount === 1 ? '' : 's'}`
}
