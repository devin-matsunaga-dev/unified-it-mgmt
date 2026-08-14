import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { AlertTriangle, ClipboardList, History, PencilLine, SlidersHorizontal, Ticket } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { assetsApi, type CiTimelineEntry, type CiTimelineEventKind } from '../../api/assets'
import { cn } from '../../lib/utils'
import { PriorityPill, formatLocal } from '../tickets/ticketUi'
import {
  describeTimeline,
  describeTruncation,
  groupByDay,
  timelineDayLabel,
  timelineDot,
  timelineKindLabel,
  timelineKinds,
} from './timeline'

const kindIcons: Record<CiTimelineEventKind, typeof History> = {
  Alert: AlertTriangle,
  Ticket: Ticket,
  Lifecycle: SlidersHorizontal,
  Config: PencilLine,
}

/**
 * Everything that has happened to one CI on one axis (WP-5.3): what alerted, what was raised about it,
 * how it moved through its life and who edited its record — newest first, with a filter per kind.
 *
 * The filter is sent to the server rather than applied here. Filtering in the browser would mean fetching
 * four sources to render one, and — worse — it would make "alerts only" show the newest fifty *events*
 * rather than the newest fifty alerts, so a busy asset's alert filter would come back nearly empty.
 */
export function CiTimelinePanel({ ciId }: { ciId: string }) {
  const [kinds, setKinds] = useState<CiTimelineEventKind[]>([])

  const timeline = useQuery({
    queryKey: ['cis', ciId, 'timeline', kinds],
    queryFn: () => assetsApi.getTimeline(ciId, { types: kinds }),
    enabled: Boolean(ciId),
    // The rows stay on screen while a filter is being changed. A list that empties and refills on every
    // click reads as a page that lost its data.
    placeholderData: keepPreviousData,
  })

  function toggle(kind: CiTimelineEventKind) {
    setKinds((current) => current.includes(kind)
      ? current.filter((entry) => entry !== kind)
      : [...current, kind])
  }

  const filtered = kinds.length > 0
  const truncation = timeline.data ? describeTruncation(timeline.data.sources) : null

  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="border-b border-slate-200 p-5 dark:border-slate-800">
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-0">
          <h2 className="font-semibold">Timeline</h2>
          <p className="mt-1 text-sm text-slate-500">
            {timeline.data
              ? describeTimeline(timeline.data.summary, filtered)
              : 'Everything recorded against this asset, most recent first.'}
          </p>
        </div>
        <div className="flex flex-wrap gap-1.5 sm:ml-auto" role="group" aria-label="Filter the timeline">
          <FilterChip label="All" active={!filtered} onClick={() => setKinds([])} />
          {timelineKinds.map((kind) => <FilterChip key={kind}
            label={timelineKindLabel(kind)}
            active={kinds.includes(kind)}
            onClick={() => toggle(kind)} />)}
        </div>
      </div>
      {truncation && <p className="mt-3 flex items-center gap-1.5 text-xs text-amber-700 dark:text-amber-400">
        <AlertTriangle size={13} aria-hidden />
        More history than fits — {truncation}. Narrow it with the filters above.
      </p>}
    </div>

    {timeline.isLoading
      ? <div aria-label="Loading timeline" className="space-y-2 p-5">
          {Array.from({ length: 4 }, (_, index) => <div key={index}
            className="h-14 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}
        </div>
      // "Nothing happened" is a claim about the asset and a failed read is a fact about the request.
      // The two must not read the same (the WP-2.11 rule).
      : timeline.isError || !timeline.data
        ? <p role="alert" className="p-5 text-sm text-red-600">The timeline could not be loaded.</p>
        : timeline.data.entries.length === 0
          ? <EmptyTimeline filtered={filtered} />
          // Named, and one list item per day rather than per event: a screen reader announcing "list of
          // 5" should be announcing days, with each day's events as a list of their own inside it.
          : <ol aria-label="Timeline by day" className="divide-y divide-slate-200 dark:divide-slate-800">
              {groupByDay(timeline.data.entries).map((day) => <li key={day.key}>
                <p className="bg-slate-50 px-5 py-1.5 text-[13px] font-medium text-slate-500 dark:bg-slate-800/50">
                  {timelineDayLabel(day.entries[0].occurredAt)}
                </p>
                <ol aria-label={`Events on ${timelineDayLabel(day.entries[0].occurredAt)}`}>
                  {day.entries.map((entry) => <Entry key={`${entry.kind}-${entry.id}`} entry={entry} />)}
                </ol>
              </li>)}
            </ol>}
  </section>
}

function FilterChip({ label, active, onClick }: { label: string; active: boolean; onClick: () => void }) {
  return <button type="button" onClick={onClick} aria-pressed={active}
    className={cn('rounded-md px-2.5 py-1 text-[13px] font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
      active
        ? 'bg-blue-600 text-white'
        : 'border border-slate-200 text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800')}>
    {label}
  </button>
}

/**
 * One row: a coloured dot on a rule, the sentence the server composed, and — where the event has a page
 * of its own — a way to open it.
 */
function Entry({ entry }: { entry: CiTimelineEntry }) {
  const Icon = kindIcons[entry.kind]
  return <li className="flex gap-3 px-5 py-3">
    <div className="flex flex-col items-center pt-1.5">
      <span className={cn('size-2 shrink-0 rounded-full', timelineDot(entry))} aria-hidden />
      <span className="mt-1 w-px flex-1 bg-slate-200 dark:bg-slate-800" aria-hidden />
    </div>
    <div className="min-w-0 flex-1">
      <div className="flex flex-wrap items-center gap-2">
        <Icon size={15} className="shrink-0 text-slate-400" aria-hidden />
        <span className="sr-only">{timelineKindLabel(entry.kind)}</span>
        <EntryTitle entry={entry} />
        {entry.priority && <PriorityPill priority={entry.priority as 'Low' | 'Medium' | 'High' | 'Critical'} />}
        {entry.status && <span className="rounded-md bg-slate-100 px-1.5 py-0.5 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
          {entry.status}
        </span>}
        <span className="ml-auto shrink-0 text-xs text-slate-500">{formatLocal(entry.occurredAt)}</span>
      </div>
      {entry.detail && <p className="mt-0.5 text-[13px] text-slate-500">{entry.detail}</p>}
      <p className="mt-0.5 flex flex-wrap gap-x-2 text-xs text-slate-500">
        {entry.actor && <span>{entry.actor}</span>}
        {/* Said only when the two are apart: the entry sits where the ticket was raised, and this is
            where somebody decided it was about this asset. */}
        {entry.linkedAt && <span>linked to this asset {formatLocal(entry.linkedAt)}</span>}
      </p>
    </div>
  </li>
}

function EntryTitle({ entry }: { entry: CiTimelineEntry }) {
  if (entry.ticketId) {
    return <Link to={`/tickets/${entry.ticketId}`}
      className="min-w-0 truncate font-medium text-blue-600 hover:underline dark:text-blue-400">
      <span className="font-mono text-xs">{entry.ticketNumber}</span> {entry.title}
    </Link>
  }

  // The board's existing deep link (WP-3.10 notifications arrive at the same place), so an alert on the
  // axis opens the same drawer as an alert on the board.
  if (entry.alertId) {
    return <Link to={`/monitoring/alerts?alertId=${entry.alertId}`}
      className="min-w-0 truncate font-medium text-blue-600 hover:underline dark:text-blue-400">
      {entry.title}
    </Link>
  }

  return <span className="min-w-0 truncate font-medium">{entry.title}</span>
}

/**
 * Not "No data", and not the same sentence in both cases: an asset that has never alerted, seen through
 * the alerts filter, is a correct empty answer about alerts rather than an asset nothing has happened to.
 *
 * Which of the two it is has already been said in the subtitle by `describeTimeline`, so this says the
 * thing the subtitle cannot — what to do next.
 */
function EmptyTimeline({ filtered }: { filtered: boolean }) {
  return <div className="grid place-items-center p-8 text-center">
    <div>
      <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15">
        {filtered ? <ClipboardList /> : <History />}
      </span>
      <p className="mt-3 text-sm text-slate-500">
        {filtered
          ? 'Choose "All" to see the rest of this asset\'s history.'
          : 'Alerts, tickets, lifecycle moves and record edits all land here.'}
      </p>
    </div>
  </div>
}
