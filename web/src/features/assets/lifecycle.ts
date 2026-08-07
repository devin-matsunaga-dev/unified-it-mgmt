import type { CiAssignmentAction, CiAssignmentEntry, CiLifecycleState, CiLifecycleStateInfo } from '../../api/assets'

export const ciLifecycleStates: CiLifecycleState[] = ['Ordered', 'InStock', 'Deployed', 'InRepair', 'Retired', 'Disposed']

const lifecycleLabels: Record<CiLifecycleState, string> = {
  Ordered: 'Ordered',
  InStock: 'In stock',
  Deployed: 'Deployed',
  InRepair: 'In repair',
  Retired: 'Retired',
  Disposed: 'Disposed',
}

/** Pill classes per DESIGN.md §3: in-progress blue for deployed, warning amber for repair, neutral for the ends. */
const lifecycleTones: Record<CiLifecycleState, string> = {
  Ordered: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
  InStock: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400',
  Deployed: 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400',
  InRepair: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  Retired: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
  Disposed: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400',
}

// Both take a plain string: a ticket's linked-asset card carries the state as text from the CMDB port,
// and an unknown value has to render rather than throw.
export function ciLifecycleLabel(state: string) {
  return lifecycleLabels[state as CiLifecycleState] ?? state
}

export function ciLifecycleTone(state: string) {
  return lifecycleTones[state as CiLifecycleState] ?? lifecycleTones.Ordered
}

const assignmentLabels: Record<CiAssignmentAction, string> = {
  CheckOut: 'Checked out',
  CheckIn: 'Checked in',
  Transfer: 'Transferred',
  Relocate: 'Moved',
}

export function ciAssignmentLabel(action: CiAssignmentAction) {
  return assignmentLabels[action] ?? action
}

/** The states a CI may move to next, straight from the server's graph — never derived in the browser. */
export function allowedTargets(states: CiLifecycleStateInfo[], state: CiLifecycleState): CiLifecycleState[] {
  return states.find((entry) => entry.state === state)?.allowedTargets ?? []
}

/** One line of plain English per check-in/out row, so the log reads as a story rather than ids. */
export function describeAssignment(entry: CiAssignmentEntry): string {
  const place = [entry.departmentName, entry.siteName].filter(Boolean).join(' · ')
  switch (entry.action) {
    case 'CheckOut':
      return `${entry.toOwnerName ?? 'Someone'} took it out${place ? ` (${place})` : ''}`
    case 'CheckIn':
      return `${entry.fromOwnerName ?? 'The previous owner'} returned it${place ? ` to ${place}` : ''}`
    case 'Transfer':
      return `${entry.fromOwnerName ?? 'Someone'} handed it to ${entry.toOwnerName ?? 'someone else'}`
    default:
      return place ? `Moved to ${place}` : 'Placement cleared'
  }
}
