import type { ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { cn } from '../../lib/utils'

const tabs = [
  { to: '/monitoring', label: 'Status board', end: true },
  { to: '/monitoring/alerts', label: 'Alerts', end: false },
]

/**
 * The two boards sit under one nav item, because they answer the same question from two directions —
 * "what does the estate look like" and "what is wrong right now" — and a sidebar entry each would
 * make the shell's Monitoring item mean nothing.
 */
export function MonitoringTabs({ right }: { right?: ReactNode }) {
  return <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 pb-3 dark:border-slate-800">
    <nav aria-label="Monitoring views" className="flex gap-1">
      {tabs.map((tab) => <NavLink key={tab.to} to={tab.to} end={tab.end}
        className={({ isActive }) => cn(
          'rounded-lg px-3 py-1.5 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
          isActive
            ? 'bg-blue-600 text-white'
            : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800')}>
        {tab.label}
      </NavLink>)}
    </nav>
    {right && <div className="ml-auto">{right}</div>}
  </div>
}
