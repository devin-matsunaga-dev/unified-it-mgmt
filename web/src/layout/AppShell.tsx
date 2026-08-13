import { Bell, Boxes, Radar, FileText, ChevronRight, CircleHelp, Contact, Gauge, Headphones, LogOut, Mail, Menu, MonitorCog, ScanLine, Search, Settings, ShieldCheck, Users, X } from 'lucide-react'
import { useEffect, useRef, useState, type ComponentType } from 'react'
import { NavLink, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import type { AppRole } from '../auth/auth'
import { cn } from '../lib/utils'
import { ThemeToggle } from '../components/ThemeToggle'
import { Button } from '../components/ui/Button'

type NavItem = { label: string; to: string; icon: ComponentType<{ size?: number }>; roles?: AppRole[] }
const navigation: NavItem[] = [
  { label: 'Overview', to: '/', icon: Gauge },
  { label: 'Tickets', to: '/tickets', icon: Headphones },
  { label: 'Assets', to: '/assets', icon: Boxes, roles: ['Admin', 'Technician', 'Manager'] },
  { label: 'Discovery', to: '/assets/discovery', icon: Radar, roles: ['Admin', 'Technician', 'Manager'] },
  { label: 'Contracts', to: '/contracts', icon: FileText, roles: ['Admin', 'Technician', 'Manager'] },
  { label: 'Scan', to: '/scan', icon: ScanLine, roles: ['Admin', 'Technician', 'Manager'] },
  { label: 'Monitoring', to: '/monitoring', icon: MonitorCog, roles: ['Admin', 'Technician', 'Manager'] },
  { label: 'People', to: '/people', icon: Contact, roles: ['Admin', 'Technician', 'Manager'] },
  { label: 'Users', to: '/admin/users', icon: Users, roles: ['Admin'] },
  { label: 'Settings', to: '/admin/settings', icon: Settings, roles: ['Admin'] },
]

export function AppShell() {
  const { pathname } = useLocation()
  const { user, roles, signOut } = useAuth()
  const [open, setOpen] = useState(false)
  const [accountOpen, setAccountOpen] = useState(false)
  const accountMenuRef = useRef<HTMLDivElement>(null)
  const visibleNavigation = navigation.filter((item) => !item.roles || item.roles.some((role) => roles.includes(role)))
  const displayName = user?.profile.name ?? user?.profile.preferred_username ?? 'User'
  const email = user?.profile.email
  const pageContext = pathname.startsWith('/tickets')
    ? { title: 'Tickets', subtitle: 'Manage requests across the service desk.' }
    : pathname.startsWith('/monitoring')
      ? { title: 'Monitoring', subtitle: 'Live device status and alerts across the estate.' }
      : { title: 'Overview', subtitle: 'Your unified IT environment at a glance.' }

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
    <Button variant="secondary" className="fixed left-4 top-4 z-50 size-10 p-0 lg:hidden" aria-label="Open navigation" onClick={() => setOpen(true)}><Menu /></Button>
    {open && <button className="fixed inset-0 z-30 bg-slate-950/50 lg:hidden" aria-label="Close navigation" onClick={() => setOpen(false)} />}
    <aside className={cn('fixed inset-y-0 left-0 z-40 flex w-[232px] flex-col bg-slate-900 px-3 py-5 text-white transition-transform lg:translate-x-0', open ? 'translate-x-0' : '-translate-x-full')}>
      <div className="mb-6 flex h-10 items-center gap-3 border-b border-slate-800 px-2 pb-5 box-content">
        <div className="grid size-9 place-items-center rounded-lg bg-blue-600"><ShieldCheck size={22} /></div><span className="text-xl font-bold">ITManager</span>
        <button className="ml-auto lg:hidden" aria-label="Close navigation" onClick={() => setOpen(false)}><X /></button>
      </div>
      <nav className="space-y-1" aria-label="Primary navigation">
        {visibleNavigation.map(({ label, to, icon: Icon }) => <NavLink key={to} to={to} end={to === '/'} onClick={() => setOpen(false)} className={({ isActive }) => cn('flex h-10 items-center gap-3 rounded-lg px-3 text-sm text-slate-400 transition-colors hover:bg-slate-800 hover:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500', isActive && 'bg-blue-600 text-white hover:bg-blue-600')}><Icon size={20} /><span>{label}</span></NavLink>)}
      </nav>
      <div ref={accountMenuRef} className="relative mt-auto border-t border-slate-800 pt-4">
        {accountOpen && <div role="menu" aria-label="Account menu" className="absolute bottom-[calc(100%+8px)] left-0 w-full rounded-xl border border-slate-700 bg-slate-800 p-2 shadow-lg">
          <div className="border-b border-slate-700 px-2 pb-3 pt-1">
            <p className="truncate text-sm font-semibold">{String(displayName)}</p>
            {email && <p className="mt-1 flex items-center gap-1.5 truncate text-xs text-slate-400"><Mail size={13} />{String(email)}</p>}
            <p className="mt-2 text-xs text-slate-400">Role: <span className="text-slate-200">{roles.join(', ') || 'No role assigned'}</span></p>
          </div>
          <div className="flex items-center justify-between px-2 py-2 text-sm text-slate-300"><span>Theme preference</span><ThemeToggle /></div>
          <button role="menuitem" onClick={() => void signOut()} className="flex h-9 w-full items-center gap-2 rounded-lg px-2 text-sm text-slate-300 hover:bg-slate-700 hover:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"><LogOut size={17} />Sign out</button>
        </div>}
        <button aria-haspopup="menu" aria-expanded={accountOpen} onClick={() => setAccountOpen((current) => !current)} className="flex w-full items-center gap-3 rounded-lg p-2 text-left hover:bg-slate-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500">
          <span className="grid size-9 place-items-center rounded-full bg-blue-100 font-semibold text-blue-700">{String(displayName).charAt(0).toUpperCase()}</span>
          <span className="min-w-0 flex-1"><span className="block truncate text-sm font-medium">{String(displayName)}</span><span className="block truncate text-xs text-slate-400">{roles.join(', ')}</span></span><ChevronRight size={16} className={cn('transition-transform', accountOpen && '-rotate-90')} />
        </button>
      </div>
    </aside>
    <main className="min-h-screen lg:ml-[232px]">
      <header className="flex min-h-20 items-center gap-3 border-b border-slate-200 bg-white px-6 dark:border-slate-800 dark:bg-slate-900 lg:px-7">
        <div className="ml-12 lg:ml-0"><h1 className="text-xl font-bold sm:text-2xl">{pageContext.title}</h1><p className="hidden text-sm text-slate-500 sm:block">{pageContext.subtitle}</p></div>
        <div className="ml-auto hidden h-10 max-w-sm flex-1 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 md:flex dark:border-slate-700"><Search size={18} /><input aria-label="Global search" className="w-full bg-transparent text-sm outline-none" placeholder="Search..." /></div>
        <ThemeToggle /><Button variant="ghost" className="relative size-10 p-0" aria-label="Notifications"><Bell size={20} /><span className="absolute right-1 top-1 grid size-4 place-items-center rounded-full bg-blue-600 text-[10px] text-white">3</span></Button><Button variant="ghost" className="hidden size-10 p-0 sm:inline-flex" aria-label="Help"><CircleHelp size={20} /></Button>
      </header>
      <div className="p-4 sm:p-6"><Outlet /></div>
    </main>
  </div>
}
