import { useQuery } from '@tanstack/react-query'
import { Inbox } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { helpdeskApi } from '../../api/helpdesk'
import { useAuth } from '../../auth/AuthProvider'
import { cn } from '../../lib/utils'
import { PriorityPill, StatusPill } from '../tickets/ticketUi'

/**
 * The technician's working set on a handset (DESIGN.md §9). Compact by design: one row per ticket
 * carrying only what decides whether to open it — number, title, how urgent, where it is in the
 * workflow, and how long it has been waiting. Filters, columns, saved views and bulk actions all
 * stay on the desktop; a phone in a corridor is for the next job, not for triage.
 */

/** Everything before Resolved. A field technician's list should empty as work is done. */
const openStatuses = ['New', 'Triage', 'InProgress', 'Pending']

export function FieldTicketsPage() {
  const { account } = useAuth()
  const [mine, setMine] = useState(true)

  const tickets = useQuery({
    // The assignee a ticket records is the sign-in name, not the OIDC subject — see CurrentUser.
    queryKey: ['tickets', 'field', mine ? account?.username : 'all'],
    queryFn: () => helpdeskApi.listTickets({
      statuses: openStatuses,
      ...(mine && account?.username ? { assignedTechnicianId: account.username } : {}),
    }),
    enabled: !mine || Boolean(account?.username),
  })

  const items = tickets.data?.items ?? []

  return <>
    <h1 className="text-[22px] font-bold leading-tight">Tickets</h1>
    <p className="mt-1 text-[15px] text-slate-500">Open work, newest first.</p>

    <div role="group" aria-label="Which tickets" className="mt-4 grid grid-cols-2 gap-2">
      {[{ value: true, label: 'Mine' }, { value: false, label: 'All open' }].map((option) => <button
        key={option.label}
        type="button"
        aria-pressed={mine === option.value}
        onClick={() => setMine(option.value)}
        className={cn(
          'h-12 rounded-lg border text-[15px] font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
          mine === option.value
            ? 'border-blue-600 bg-blue-50 text-blue-700 dark:bg-blue-950 dark:text-blue-300'
            : 'border-slate-200 bg-white text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300',
        )}
      >{option.label}</button>)}
    </div>

    {tickets.isLoading
      ? <div aria-label="Loading" className="mt-4 space-y-2">
          <div className="h-20 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
          <div className="h-20 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
        </div>
      : items.length === 0
        ? <div className="mt-6 rounded-xl border border-slate-200 bg-white p-6 text-center dark:border-slate-800 dark:bg-slate-900">
            <span className="mx-auto grid size-12 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800">
              <Inbox size={22} />
            </span>
            <p className="mt-3 text-[15px] text-slate-500">
              {mine ? 'Nothing is assigned to you right now.' : 'No tickets are open.'}
            </p>
          </div>
        : <ul className="mt-3 space-y-2">
            {items.map((ticket) => <li key={ticket.id}>
              <Link
                to={`/field/tickets/${ticket.id}`}
                className="block min-h-[72px] rounded-xl border border-slate-200 bg-white p-4 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-800 dark:bg-slate-900"
              >
                <span className="flex items-center gap-2">
                  <span className="text-[13px] tabular-nums text-slate-500">{ticket.number}</span>
                  <span className="ml-auto flex shrink-0 gap-1.5">
                    <PriorityPill priority={ticket.priority} />
                    <StatusPill status={ticket.status} />
                  </span>
                </span>
                <span className="mt-1 block text-[15px] font-medium leading-snug">{ticket.title}</span>
                <span className="mt-1 block text-[13px] text-slate-500">{waitingFor(ticket.createdAt)}</span>
              </Link>
            </li>)}
          </ul>}
  </>
}

/**
 * Age in the coarsest unit that is still true. A timestamp would be exact and useless — what a
 * technician wants off this row is whether a ticket has been sitting, not when it arrived.
 */
export function waitingFor(createdAt: string, now: Date = new Date()): string {
  const minutes = Math.max(0, Math.floor((now.getTime() - new Date(createdAt).getTime()) / 60000))
  if (minutes < 60) return `${minutes}m old`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h old`
  return `${Math.floor(hours / 24)}d old`
}
