import type { CiTimelineEntry, CiTimelineEventKind, CiTimelineSource } from '../../api/assets'

/**
 * What each kind is called on the filter and above each group. Sentence case per DESIGN.md §4, and
 * "Configuration" rather than "Audit" because the row an operator reads is an edit somebody made — the
 * audit log is where it is kept, not what it is.
 */
const kindLabels: Record<CiTimelineEventKind, string> = {
  Alert: 'Alerts',
  Ticket: 'Tickets',
  Lifecycle: 'Lifecycle',
  Config: 'Configuration',
}

export const timelineKinds: CiTimelineEventKind[] = ['Alert', 'Ticket', 'Lifecycle', 'Config']

export function timelineKindLabel(kind: CiTimelineEventKind) {
  return kindLabels[kind] ?? kind
}

/**
 * The dot beside each entry. DESIGN.md §3 is emphatic that monitoring severity keeps the same semantics
 * everywhere, so an alert's colour comes from its severity and from nothing else — a Critical that
 * recovered a month ago is still drawn in the red family, because that is what happened.
 *
 * Everything that is not an alert is neutral. A lifecycle move and a record edit are things somebody
 * chose to do; colouring them by health would make an ordinary Tuesday afternoon look like an incident.
 */
export function timelineDot(entry: Pick<CiTimelineEntry, 'kind' | 'severity'>): string {
  if (entry.kind !== 'Alert') return 'bg-slate-300 dark:bg-slate-600'
  if (entry.severity === 'Critical') return 'bg-red-600'
  if (entry.severity === 'Warning') return 'bg-amber-600'
  return 'bg-green-600'
}

/**
 * The day heading an entry belongs under, as a stable key. Local time, because an operator reading
 * "what happened on the 3rd" means their 3rd — the instant travels as UTC and is converted here, which
 * is the only place it may be (DESIGN.md §10).
 */
export function timelineDayKey(occurredAt: string): string {
  const at = new Date(occurredAt)
  return `${at.getFullYear()}-${String(at.getMonth() + 1).padStart(2, '0')}-${String(at.getDate()).padStart(2, '0')}`
}

export function timelineDayLabel(occurredAt: string, now: Date = new Date()): string {
  const key = timelineDayKey(occurredAt)
  if (key === timelineDayKey(now.toISOString())) return 'Today'
  const yesterday = new Date(now)
  yesterday.setDate(yesterday.getDate() - 1)
  if (key === timelineDayKey(yesterday.toISOString())) return 'Yesterday'
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'full' }).format(new Date(occurredAt))
}

/**
 * The entries grouped into days, order preserved. The server already ordered them newest first and this
 * must not re-sort: two renderings of one answer disagreeing about the order is exactly the bug a
 * timeline cannot afford.
 */
export function groupByDay(entries: readonly CiTimelineEntry[]): { key: string; entries: CiTimelineEntry[] }[] {
  const days: { key: string; entries: CiTimelineEntry[] }[] = []
  for (const entry of entries) {
    const key = timelineDayKey(entry.occurredAt)
    const last = days.at(-1)
    if (last?.key === key) last.entries.push(entry)
    else days.push({ key, entries: [entry] })
  }
  return days
}

/**
 * The line under the heading: how much of the CI's history is on screen, and how much is not.
 *
 * It never says "no history" when a filter is on. A timeline filtered to alerts on an asset that has
 * never alerted is a correct empty answer about alerts, and reading it as an asset nothing has happened
 * to is the single most likely misreading of this screen.
 */
export function describeTimeline(
  summary: { entryCount: number; totalCount: number; truncated: boolean },
  filtered: boolean,
): string {
  if (summary.entryCount === 0) {
    return filtered ? 'Nothing of the kinds you selected' : 'Nothing has been recorded against this asset yet'
  }
  const shown = `${summary.entryCount} event${summary.entryCount === 1 ? '' : 's'}`
  return summary.truncated ? `${shown} of ${summary.totalCount}, most recent first` : `${shown}, most recent first`
}

/**
 * What a source that ran out of room has to say for itself, or null when it had none to hide. Named per
 * source rather than once for the axis, because "the alerts are truncated" and "everything is truncated"
 * send an operator to different places.
 */
export function describeTruncation(sources: readonly CiTimelineSource[]): string | null {
  const cut = sources.filter((source) => source.truncated)
  if (cut.length === 0) return null
  return cut
    .map((source) => `${timelineKindLabel(source.kind).toLowerCase()}: showing ${source.returned} of ${source.total}`)
    .join(' · ')
}
