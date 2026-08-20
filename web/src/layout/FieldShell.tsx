import { ClipboardCheck, LogOut, ScanLine, Ticket } from 'lucide-react'
import { Link, NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import { cn } from '../lib/utils'

/**
 * The technician's surface on a handset (DESIGN.md §9). Deliberately not the agent shell: that one is
 * built to a 1280px floor and a field technician is holding a phone in one hand and an asset in the
 * other. Same tokens, one column, no sidebar, no tables — and the actions live at the bottom of each
 * screen rather than the top, which is the only part of a phone a thumb reaches comfortably.
 */
export function FieldShell() {
  const { signOut } = useAuth()

  return <div className="min-h-screen bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
    <header className="sticky top-0 z-10 border-b border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="mx-auto flex h-14 max-w-[520px] items-center gap-3 px-4">
        <Link to="/field/scan" className="flex items-center gap-2 rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600">
          <span className="grid size-8 place-items-center rounded-lg bg-blue-600 text-white"><ScanLine size={19} /></span>
          <span className="text-base font-bold">Field</span>
        </Link>
        <nav aria-label="Field navigation" className="ml-auto flex items-center gap-1">
          {[
            { to: '/field/scan', label: 'Scan', icon: ScanLine },
            { to: '/field/tickets', label: 'Tickets', icon: Ticket },
            { to: '/field/audits', label: 'Counts', icon: ClipboardCheck },
          ].map(({ to, label, icon: Icon }) => <NavLink
            key={to}
            to={to}
            aria-label={label}
            className={({ isActive }) => cn(
              'grid size-11 place-items-center rounded-lg text-slate-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
              isActive && 'bg-blue-50 text-blue-700 dark:bg-blue-950 dark:text-blue-300',
            )}
          ><Icon size={19} /></NavLink>)}
        </nav>
        <button
          onClick={() => void signOut()}
          aria-label="Sign out"
          className="grid size-11 shrink-0 place-items-center rounded-lg text-slate-500 hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800"
        ><LogOut size={19} /></button>
      </div>
    </header>
    {/*
      pb-32 leaves room for the action bar each screen anchors to the bottom, so the last card is
      never trapped underneath it; the safe-area inset keeps it clear of the iOS home indicator.
    */}
    <main className="mx-auto max-w-[520px] px-4 pb-32 pt-4 [padding-bottom:calc(8rem+env(safe-area-inset-bottom))]">
      <Outlet />
    </main>
  </div>
}

/**
 * The bottom-anchored action bar from DESIGN.md §9. Fixed rather than in flow: a technician should
 * not have to scroll a screen to reach the thing they opened it to do.
 */
export function FieldActionBar({ children }: { children: React.ReactNode }) {
  return <div className="fixed inset-x-0 bottom-0 z-10 border-t border-slate-200 bg-white/95 backdrop-blur dark:border-slate-800 dark:bg-slate-900/95">
    <div className="mx-auto flex max-w-[520px] flex-col gap-2 px-4 py-3 [padding-bottom:calc(0.75rem+env(safe-area-inset-bottom))]">
      {children}
    </div>
  </div>
}
