import { useQuery } from '@tanstack/react-query'
import { ClipboardCheck, MapPin } from 'lucide-react'
import { Link } from 'react-router-dom'
import { reconciliationApi } from '../../api/reconciliation'

/**
 * The open counts a technician can join. Only open ones: a closed session refuses scans with a 409,
 * and offering one on a phone would mean walking a floor to be told at the end that none of it
 * counted. Creating a session stays on the desktop — it needs a site and a name, which is desk work.
 */
export function FieldAuditsPage() {
  const sessions = useQuery({
    queryKey: ['audit-sessions', 'Open', 'field'],
    queryFn: () => reconciliationApi.listAuditSessions('Open', 1, 50),
  })

  if (sessions.isLoading) {
    return <div aria-label="Loading" className="space-y-3">
      <div className="h-20 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
      <div className="h-20 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
    </div>
  }

  const items = sessions.data?.items ?? []

  return <>
    <h1 className="text-[22px] font-bold leading-tight">Stock counts</h1>
    <p className="mt-1 text-[15px] text-slate-500">Pick the count you are walking.</p>

    {items.length === 0
      ? <div className="mt-6 rounded-xl border border-slate-200 bg-white p-6 text-center dark:border-slate-800 dark:bg-slate-900">
          <span className="mx-auto grid size-12 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800">
            <ClipboardCheck size={22} />
          </span>
          <p className="mt-3 text-[15px] text-slate-500">No count is open. One has to be started from the desktop before you can walk it.</p>
        </div>
      : <ul className="mt-5 space-y-2">
          {items.map((session) => <li key={session.id}>
            <Link
              to={`/field/audits/${session.id}`}
              className="flex min-h-[72px] items-center gap-3 rounded-xl border border-slate-200 bg-white p-4 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-800 dark:bg-slate-900"
            >
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[17px] font-semibold">{session.name}</span>
                <span className="mt-0.5 flex items-center gap-1.5 text-[13px] text-slate-500">
                  {session.siteName && <><MapPin size={14} />{session.siteName} · </>}
                  {session.scanCount} scanned
                </span>
              </span>
            </Link>
          </li>)}
        </ul>}
  </>
}
