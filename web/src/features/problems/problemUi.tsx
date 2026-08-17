import { cn } from '../../lib/utils'
import type { ProblemStatus, ProblemSubject } from '../../api/problems'

/** The lifecycle in order, which is what the filter chips and the transition buttons both read. */
export const problemStatuses: ProblemStatus[] = ['Investigating', 'KnownError', 'Resolved', 'Closed']

export const problemStatusLabel = (status: ProblemStatus) =>
  status === 'KnownError' ? 'Known error' : status

/**
 * Where a problem can go next, mirroring `ProblemWorkflow` on the server.
 *
 * Duplicated deliberately and narrowly: this decides which buttons are *enabled*, and the server decides
 * what actually happens — a browser that guessed wrong gets a 409 with the reason in it. The alternative,
 * an endpoint that hands the SPA its own workflow, would be a round trip to grey out four buttons.
 */
export const problemNextStatuses = (status: ProblemStatus): ProblemStatus[] => {
  switch (status) {
    case 'Investigating': return ['KnownError', 'Resolved', 'Closed']
    case 'KnownError': return ['Investigating', 'Resolved', 'Closed']
    case 'Resolved': return ['Closed', 'Investigating']
    case 'Closed': return ['Investigating']
  }
}

/** DESIGN §3's semantic families: in progress is blue, a published workaround is amber, an ending is green. */
export function ProblemStatusPill({ status }: { status: ProblemStatus }) {
  return <span className={cn(
    'inline-flex rounded-md px-2 py-0.5 text-xs font-medium',
    status === 'Investigating' && 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300',
    status === 'KnownError' && 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300',
    status === 'Resolved' && 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300',
    status === 'Closed' && 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
  )}>{problemStatusLabel(status)}</span>
}

/**
 * What a problem is about, in words. A CI whose name came back null is one that has been deleted — a
 * problem outlives the thing it was about, and saying so is truer than showing a bare id or nothing.
 */
export function subjectLabel(subject: ProblemSubject | null) {
  if (!subject) return 'No configuration item or category'
  if (subject.name) return subject.name
  return subject.scope === 'Ci' ? 'A configuration item that no longer exists' : 'An unnamed category'
}

/** Where clicking the subject goes. A category has no page of its own, so it filters the ticket list. */
export const subjectHref = (subject: ProblemSubject) =>
  subject.scope === 'Ci' ? `/assets/${subject.id}` : `/tickets?categoryId=${subject.id}`

/** "5 incidents in 7 days" — the sentence a suggestion is. */
export function windowSummary(incidentCount: number, windowStart: string, windowEnd: string) {
  const days = Math.max(1, Math.round(
    (new Date(windowEnd).getTime() - new Date(windowStart).getTime()) / 86_400_000))
  return `${incidentCount} incident${incidentCount === 1 ? '' : 's'} in ${days} day${days === 1 ? '' : 's'}`
}

/** The detector's own vocabulary, said in English on the run summary. */
export const skipReasonLabel = (reason: string) => ({
  BelowThreshold: 'below the threshold',
  AlreadyAProblem: 'already a problem',
  AlreadySuggested: 'already suggested',
  DismissalStillHolds: 'dismissed recently',
  OverRunLimit: 'beyond this run’s limit',
} as Record<string, string>)[reason] ?? reason
