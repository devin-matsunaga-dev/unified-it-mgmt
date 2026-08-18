import { BookOpen, ChevronDown, LifeBuoy, LogOut, Mail, Plus, ShieldCheck } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import { cn } from '../lib/utils'
import { ThemeToggle } from '../components/ThemeToggle'
import { Button } from '../components/ui/Button'

const navigation = [
  { label: 'My requests', to: '/portal', icon: LifeBuoy, end: true },
  // Before "New request", deliberately: the point of a help centre is that reading comes before raising.
  { label: 'Help articles', to: '/portal/kb', icon: BookOpen, end: false },
  { label: 'New request', to: '/portal/new', icon: Plus, end: false },
]

export function PortalShell() {
  const { user, signOut } = useAuth()
  const [accountOpen, setAccountOpen] = useState(false)
  const accountMenuRef = useRef<HTMLDivElement>(null)
  const displayName = String(user?.profile.name ?? user?.profile.preferred_username ?? 'User')
  const email = user?.profile.email

  useEffect(() => {
    if (!accountOpen) return
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (!accountMenuRef.current?.contains(event.target as Node)) setAccountOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setAccountOpen(false)
    }
    document.addEventListener('mousedown', closeOnOutsideClick)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('mousedown', closeOnOutsideClick)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [accountOpen])

  return <div className="min-h-screen bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
    <header className="border-b border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="mx-auto flex min-h-[72px] max-w-[960px] flex-wrap items-center gap-x-4 gap-y-3 px-6 py-3">
        <NavLink to="/portal" end className="flex items-center gap-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500">
          <span className="grid size-9 place-items-center rounded-lg bg-blue-600 text-white"><ShieldCheck size={22} /></span>
          <span className="text-lg font-bold">Help centre</span>
        </NavLink>
        <nav aria-label="Portal navigation" className="flex items-center gap-1">
          {navigation.map(({ label, to, icon: Icon, end }) => <NavLink key={to} to={to} end={end} className={({ isActive }) => cn('flex h-10 items-center gap-2 rounded-lg px-3 text-sm font-medium text-slate-600 transition-colors hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:text-slate-300 dark:hover:bg-slate-800', isActive && 'bg-blue-50 text-blue-700 hover:bg-blue-50 dark:bg-blue-950 dark:text-blue-300')}><Icon size={18} />{label}</NavLink>)}
        </nav>
        <div ref={accountMenuRef} className="relative ml-auto flex items-center gap-2">
          <ThemeToggle />
          <button aria-haspopup="menu" aria-expanded={accountOpen} onClick={() => setAccountOpen((current) => !current)} className="flex h-10 items-center gap-2 rounded-lg px-2 text-left hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:hover:bg-slate-800">
            <span className="grid size-8 place-items-center rounded-full bg-blue-100 text-sm font-semibold text-blue-700">{displayName.charAt(0).toUpperCase()}</span>
            <span className="hidden max-w-36 truncate text-sm font-medium sm:block">{displayName}</span>
            <ChevronDown size={16} className={cn('transition-transform', accountOpen && 'rotate-180')} />
          </button>
          {accountOpen && <div role="menu" aria-label="Account menu" className="absolute right-0 top-[calc(100%+8px)] z-20 w-64 rounded-xl border border-slate-200 bg-white p-2 shadow-sm dark:border-slate-700 dark:bg-slate-800">
            <div className="border-b border-slate-200 px-2 pb-3 pt-1 dark:border-slate-700">
              <p className="truncate text-sm font-semibold">{displayName}</p>
              {email && <p className="mt-1 flex items-center gap-1.5 truncate text-xs text-slate-500"><Mail size={13} />{String(email)}</p>}
            </div>
            <button role="menuitem" onClick={() => void signOut()} className="mt-1 flex h-9 w-full items-center gap-2 rounded-lg px-2 text-sm text-slate-600 hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:text-slate-300 dark:hover:bg-slate-700"><LogOut size={17} />Sign out</button>
          </div>}
        </div>
      </div>
    </header>
    <main className="mx-auto max-w-[960px] px-6 py-10"><Outlet /></main>
  </div>
}

export function PortalErrorState({ title, message, retry }: { title: string; message: string; retry: () => void }) {
  return <div role="alert" className="rounded-xl border border-slate-200 bg-white p-10 text-center dark:border-slate-800 dark:bg-slate-900">
    <h2 className="text-lg font-semibold">{title}</h2>
    <p className="mx-auto mt-2 max-w-md text-sm text-slate-500">{message}</p>
    <Button className="mt-5" variant="secondary" onClick={retry}>Try again</Button>
  </div>
}
