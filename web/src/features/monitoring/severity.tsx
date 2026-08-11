import { cn } from '../../lib/utils'
import type { AlertSeverity, DeviceStatus } from '../../api/monitoring'

/**
 * DESIGN.md §3: monitoring severity uses the semantic colours everywhere — a Critical is always this
 * red family, on a tile, a pill, a chart or a map. These two maps are the single place that is
 * decided, so a board and a chart can never disagree about what red means.
 */
export const severityTone: Record<AlertSeverity, string> = {
  Critical: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-300',
  Warning: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300',
  Ok: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-300',
}

export const statusTone: Record<DeviceStatus, string> = {
  Critical: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-300',
  Warning: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300',
  Ok: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-300',
  // Neither of these is a health claim, so neither gets a health colour (DESIGN.md §3: neutral).
  Unknown: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
  Disabled: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
}

export const statusDot: Record<DeviceStatus, string> = {
  Critical: 'bg-red-600',
  Warning: 'bg-amber-600',
  Ok: 'bg-green-600',
  Unknown: 'bg-slate-400',
  Disabled: 'bg-slate-400',
}

export const statusLabel: Record<DeviceStatus, string> = {
  Critical: 'Critical',
  Warning: 'Warning',
  Ok: 'Healthy',
  Unknown: 'Not yet reported',
  Disabled: 'Disabled',
}

export function SeverityPill({ severity }: { severity: AlertSeverity }) {
  return <span className={cn('inline-flex rounded-md px-2 py-0.5 text-xs font-medium', severityTone[severity])}>
    {severity}
  </span>
}

export function StatusPill({ status }: { status: DeviceStatus }) {
  return <span className={cn('inline-flex rounded-md px-2 py-0.5 text-xs font-medium', statusTone[status])}>
    {statusLabel[status]}
  </span>
}

/** Local time, muted everywhere it appears (DESIGN.md §10). */
export function formatLocal(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
}

/** "4m ago" — how long a problem has been going on, which is the number an operator reads first. */
export function formatAge(value: string, now: number = Date.now()) {
  const seconds = Math.max(0, Math.floor((now - new Date(value).getTime()) / 1000))
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86_400)}d ago`
}
