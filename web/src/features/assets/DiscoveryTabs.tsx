import type { ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { cn } from '../../lib/utils'

const tabs = [
  { to: '/assets/discovery', label: 'Review queue', end: true },
  { to: '/assets/discovery/profiles', label: 'Scan profiles', end: false },
]

/**
 * What the scans found, and where the scans look. Two halves of one nav item, following
 * `MonitoringTabs`: an operator who wants to change a range got here by asking "why has discovery not
 * found this", and sending them to a settings screen in another part of the app to answer it would
 * make the Discovery nav entry mean only half of what it says.
 *
 * The profiles are Monitoring's data behind `CanManageMonitoring`, while the queue is Assets' behind
 * `CanManageAssets` — they sit together because that is how the work reads, not because one module
 * owns both.
 */
export function DiscoveryTabs({ right }: { right?: ReactNode }) {
  return <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 pb-3 dark:border-slate-800">
    <nav aria-label="Discovery views" className="flex gap-1">
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
