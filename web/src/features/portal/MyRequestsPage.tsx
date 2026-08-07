import { useQuery } from '@tanstack/react-query'
import { ChevronRight, Inbox, Plus } from 'lucide-react'
import { Link, useNavigate } from 'react-router-dom'
import { helpdeskApi } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { PortalErrorState } from '../../layout/PortalShell'
import { StatusPill, formatLocal } from '../tickets/ticketUi'

export function MyRequestsPage() {
  const navigate = useNavigate()
  const requests = useQuery({ queryKey: ['tickets'], queryFn: () => helpdeskApi.listTickets() })
  const items = requests.data?.items ?? []

  return <div className="space-y-8">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[32px] font-bold leading-tight">My requests</h1>
        <p className="mt-2 text-base text-slate-500">Everything you have asked the IT team for, and where it stands.</p>
      </div>
      <Button className="h-11 sm:ml-auto" onClick={() => navigate('/portal/new')}><Plus size={18} />New request</Button>
    </div>

    {requests.isLoading ? <RequestsSkeleton />
      : requests.isError ? <PortalErrorState title="Your requests could not be loaded" message={requests.error instanceof Error ? requests.error.message : 'Try again in a moment.'} retry={() => void requests.refetch()} />
      : items.length === 0 ? <div className="rounded-xl border border-slate-200 bg-white p-12 text-center dark:border-slate-800 dark:bg-slate-900">
          <span className="mx-auto grid size-14 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-950 dark:text-blue-300"><Inbox size={26} /></span>
          <h2 className="mt-4 text-lg font-semibold">You have not submitted any requests yet</h2>
          <p className="mx-auto mt-2 max-w-md text-sm text-slate-500">When you need help with a device, an account, or software, raise a request and the IT team will pick it up.</p>
          <Button className="mt-6 h-11" onClick={() => navigate('/portal/new')}><Plus size={18} />New request</Button>
        </div>
      : <ul className="space-y-3">
          {items.map((request) => <li key={request.id}>
            <Link to={`/portal/requests/${request.id}`} className="flex items-center gap-4 rounded-xl border border-slate-200 bg-white p-5 transition-colors hover:border-blue-300 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:border-slate-800 dark:bg-slate-900 dark:hover:border-blue-800">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-mono text-xs text-slate-500">#{request.number}</span>
                  <StatusPill status={request.status} />
                </div>
                <p className="mt-1.5 truncate text-base font-semibold">{request.title}</p>
                <p className="mt-1 text-sm text-slate-500">Last updated {formatLocal(request.updatedAt)}</p>
              </div>
              <ChevronRight size={20} className="shrink-0 text-slate-400" />
            </Link>
          </li>)}
        </ul>}
  </div>
}

function RequestsSkeleton() {
  return <div aria-label="Loading requests" className="space-y-3">
    {Array.from({ length: 4 }, (_, index) => <div key={index} className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />)}
  </div>
}
