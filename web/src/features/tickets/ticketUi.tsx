import { AlertTriangle, CheckCircle2, CirclePause, Clock3 } from 'lucide-react'
import { cn } from '../../lib/utils'
import type { TicketPriority } from '../../api/helpdesk'

/** The default workflow's statuses, in order — used by the list filters and the detail page's transitions. */
export const ticketStatuses = ['New', 'Triage', 'InProgress', 'Pending', 'Resolved', 'Closed'] as const
export const ticketPriorities: TicketPriority[] = ['Low', 'Medium', 'High', 'Critical']
export const displayStatus = (status: string) => status === 'InProgress' ? 'In progress' : status

export function PriorityPill({ priority }: { priority: TicketPriority }) {
  return <span className={cn('inline-flex rounded-md px-2 py-0.5 text-xs font-medium', priority === 'Low' && 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300', priority === 'Medium' && 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300', ['High', 'Critical'].includes(priority) && 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300')}>{priority}</span>
}

export function StatusPill({ status }: { status: string }) {
  const normalized = status.toLowerCase()
  return <span className={cn('inline-flex rounded-md px-2 py-0.5 text-xs font-medium', ['new', 'triage', 'inprogress', 'in progress'].includes(normalized) && 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300', normalized === 'pending' && 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300', normalized === 'resolved' && 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300', normalized === 'closed' && 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300')}>{status === 'InProgress' ? 'In progress' : status}</span>
}

export function formatLocal(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
}

export function formatRemaining(seconds: number) {
  const absolute = Math.max(0, Math.floor(Math.abs(seconds)))
  const days = Math.floor(absolute / 86400)
  const hours = Math.floor((absolute % 86400) / 3600)
  const minutes = Math.floor((absolute % 3600) / 60)
  const value = days > 0 ? `${days}d ${hours}h` : `${hours}h ${minutes}m`
  return seconds < 0 ? `${value} overdue` : value
}

export const slaIcon = (paused: boolean, remaining: number) => paused ? CirclePause : remaining < 0 ? AlertTriangle : remaining === 0 ? CheckCircle2 : Clock3
