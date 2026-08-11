import { cn } from '../../lib/utils'
import type { MonitoringHubStatus } from './useMonitoringHub'

const wording: Record<MonitoringHubStatus, { label: string; dot: string; title: string }> = {
  live: { label: 'Live', dot: 'bg-green-600', title: 'Updating as alerts change.' },
  connecting: { label: 'Connecting', dot: 'bg-slate-400', title: 'Opening the live connection.' },
  reconnecting: { label: 'Reconnecting', dot: 'bg-amber-600', title: 'The live connection dropped and is being re-established. This page will re-read everything when it returns.' },
  offline: { label: 'Not live', dot: 'bg-slate-400', title: 'No live connection — refresh to see changes.' },
}

/**
 * Whether what is on screen is actually keeping up. A board that has quietly stopped receiving looks
 * exactly like a quiet estate, and those are opposite facts — this is the only thing that tells them
 * apart.
 */
export function LiveIndicator({ status }: { status: MonitoringHubStatus }) {
  const state = wording[status]
  return <span title={state.title} className="inline-flex items-center gap-2 text-[13px] text-slate-500">
    <span className={cn('size-2 rounded-full', state.dot)} aria-hidden />
    <span role="status">{state.label}</span>
  </span>
}
