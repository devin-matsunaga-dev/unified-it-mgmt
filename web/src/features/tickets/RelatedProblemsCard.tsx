import { useQuery } from '@tanstack/react-query'
import { BookOpen, ShieldQuestion } from 'lucide-react'
import { Link } from 'react-router-dom'
import { problemsApi } from '../../api/problems'
import { ProblemStatusPill } from '../problems/problemUi'

/**
 * The problem this incident belongs to, on the incident.
 *
 * This is the half of problem management a technician actually meets. A known error with a workaround is
 * worth having only if it reaches the person holding the ticket — a database nobody is shown is a
 * database nobody reads, so the workaround is rendered here in full rather than behind a link.
 *
 * Renders nothing when the ticket belongs to no problem, which is most tickets: an empty card on every
 * ticket screen would cost more attention than it ever repaid.
 */
export function RelatedProblemsCard({ ticketId }: { ticketId: string }) {
  const problems = useQuery({
    queryKey: ['tickets', ticketId, 'problems'],
    queryFn: () => problemsApi.listForTicket(ticketId),
    enabled: Boolean(ticketId),
  })

  const items = problems.data ?? []
  if (items.length === 0) return null

  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
      <span className="grid size-10 place-items-center rounded-full bg-amber-50 text-amber-600 dark:bg-amber-500/15">
        {items.some((problem) => problem.isKnownError) ? <BookOpen size={20} /> : <ShieldQuestion size={20} />}
      </span>
      <div>
        <h2 className="font-semibold">Part of a known problem</h2>
        <p className="mt-0.5 text-sm text-slate-500">This is not an isolated fault — somebody is on the cause.</p>
      </div>
    </div>
    <ul className="divide-y divide-slate-200 dark:divide-slate-800">
      {items.map((problem) => <li key={problem.id} className="p-4">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
          <Link to={`/problems/${problem.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{problem.title}</Link>
          <span className="text-xs text-slate-500">{problem.number}</span>
          <ProblemStatusPill status={problem.status} />
          <span className="ml-auto text-xs text-slate-500">{problem.incidentCount} linked incident{problem.incidentCount === 1 ? '' : 's'}</span>
        </div>
        {problem.workaround && <div className="mt-3 rounded-lg bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">
          <p className="text-[13px] font-medium">Workaround</p>
          <p className="mt-1 whitespace-pre-wrap leading-6">{problem.workaround}</p>
        </div>}
      </li>)}
    </ul>
  </section>
}
