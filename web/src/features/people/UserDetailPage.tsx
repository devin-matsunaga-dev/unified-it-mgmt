import { useQueries, useQuery } from '@tanstack/react-query'
import { ArrowLeft, Boxes, Ticket } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { assetsApi, ciTypeLabel } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import { helpdeskApi, type Ticket as HelpdeskTicket } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { ciLifecycleLabel, ciLifecycleTone } from '../assets/lifecycle'
import { PriorityPill, StatusPill, formatLocal } from '../tickets/ticketUi'
import { usePageHeading } from '../../layout/pageHeading'

/**
 * A person's 360° page: the assets they hold and the tickets they are in. Assets are keyed by the
 * directory's user id, tickets by the identity the helpdesk recorded — for seeded data, the username.
 */
export function UserDetailPage() {
  const { userId = '' } = useParams()
  const users = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers })
  const user = users.data?.find((entry) => entry.id === userId)
  usePageHeading(user ? { title: user.displayName } : null)

  const [assets, requested, assigned] = useQueries({ queries: [
    { queryKey: ['cis', { ownerUserId: userId }], queryFn: () => assetsApi.listCis({ ownerUserId: userId, pageSize: 50 }), enabled: Boolean(userId) },
    // Named keys rather than filter objects: while the directory is still loading both filters would
    // hash to the same empty key and collide.
    { queryKey: ['tickets', 'requested-by', user?.username], queryFn: () => helpdeskApi.listTickets({ requesterId: user!.username }), enabled: Boolean(user) },
    { queryKey: ['tickets', 'assigned-to', user?.username], queryFn: () => helpdeskApi.listTickets({ assignedTechnicianId: user!.username }), enabled: Boolean(user) },
  ] })

  if (users.isLoading) return <div aria-label="Loading person" className="space-y-6"><div className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" /><div className="h-96 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" /></div>
  if (users.isError || !user) return <div role="alert" className="rounded-xl border border-red-200 bg-white p-8 text-center dark:border-red-900 dark:bg-slate-900">
    <h1 className="font-semibold">Person could not be loaded</h1>
    <p className="mt-2 text-sm text-slate-500">{users.isError ? 'The directory is unavailable.' : 'Nobody in the directory has that id.'}</p>
    <Button className="mt-4" variant="secondary" onClick={() => void users.refetch()}>Try again</Button>
  </div>

  const ownedAssets = assets.data?.items ?? []

  return <div className="space-y-6">
    <Link to="/people" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to people</Link>

    <header className="flex flex-wrap items-center gap-4">
      <span className="grid size-14 place-items-center rounded-full bg-blue-100 text-xl font-semibold text-blue-700 dark:bg-blue-500/15 dark:text-blue-300">{user.displayName.charAt(0).toUpperCase()}</span>
      <div>
        <h1 className="text-[28px] font-bold leading-tight">{user.displayName}</h1>
        <p className="mt-1 text-sm text-slate-500">{user.role} · {user.departmentName} · {user.siteName} · {user.email}</p>
      </div>
    </header>

    <div className="grid gap-6 xl:grid-cols-2">
      <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
          <div><h2 className="font-semibold">Assets</h2><p className="mt-1 text-sm text-slate-500">Configuration items checked out to {user.displayName}.</p></div>
          <span className="ml-auto text-[13px] text-slate-500">{assets.data?.total ?? 0}</span>
        </div>
        {assets.isLoading ? <Skeleton label="Loading assets" />
          : assets.isError ? <div role="alert" className="p-5 text-sm text-red-600">Assets could not be loaded.</div>
          : ownedAssets.length === 0 ? <Empty icon={<Boxes />} text="Nothing is checked out to this person." />
          : <ul className="divide-y divide-slate-200 dark:divide-slate-800">
              {ownedAssets.map((ci) => <li key={ci.id} className="flex flex-wrap items-center gap-3 p-4">
                <Link to={`/assets/${ci.id}`} className="min-w-0 flex-1 truncate font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{ci.name}</Link>
                <span className="text-xs text-slate-500">{ciTypeLabel(ci.type)}</span>
                <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(ci.lifecycleState)}`}>{ciLifecycleLabel(ci.lifecycleState)}</span>
              </li>)}
            </ul>}
      </section>

      <TicketCard title="Tickets raised" subtitle={`Requests ${user.displayName} opened.`} tickets={requested.data?.items ?? []} total={requested.data?.total ?? 0} loading={requested.isLoading} failed={requested.isError} />
      <TicketCard title="Tickets assigned" subtitle={`Work sitting with ${user.displayName}.`} tickets={assigned.data?.items ?? []} total={assigned.data?.total ?? 0} loading={assigned.isLoading} failed={assigned.isError} />
    </div>
  </div>
}

function TicketCard({ title, subtitle, tickets, total, loading, failed }: { title: string; subtitle: string; tickets: HelpdeskTicket[]; total: number; loading: boolean; failed: boolean }) {
  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
      <div><h2 className="font-semibold">{title}</h2><p className="mt-1 text-sm text-slate-500">{subtitle}</p></div>
      <span className="ml-auto text-[13px] text-slate-500">{total}</span>
    </div>
    {loading ? <Skeleton label={`Loading ${title.toLowerCase()}`} />
      : failed ? <div role="alert" className="p-5 text-sm text-red-600">Tickets could not be loaded.</div>
      : tickets.length === 0 ? <Empty icon={<Ticket />} text="No tickets to show." />
      : <ul className="divide-y divide-slate-200 dark:divide-slate-800">
          {tickets.slice(0, 15).map((ticket) => <li key={ticket.id} className="flex flex-wrap items-center gap-3 p-4">
            <span className="font-mono text-xs text-slate-500">#{ticket.number}</span>
            <Link to={`/tickets/${ticket.id}`} className="min-w-0 flex-1 truncate font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{ticket.title}</Link>
            <StatusPill status={ticket.status} />
            <PriorityPill priority={ticket.priority} />
            <span className="text-xs text-slate-500">{formatLocal(ticket.createdAt)}</span>
          </li>)}
        </ul>}
  </section>
}

function Skeleton({ label }: { label: string }) {
  return <div aria-label={label} className="space-y-2 p-5">{Array.from({ length: 3 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}</div>
}

function Empty({ icon, text }: { icon: React.ReactNode; text: string }) {
  return <div className="grid place-items-center p-8 text-center"><div>
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15">{icon}</span>
    <p className="mt-3 text-sm text-slate-500">{text}</p>
  </div></div>
}
