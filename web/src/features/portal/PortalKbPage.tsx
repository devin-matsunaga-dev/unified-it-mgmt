import { useQuery } from '@tanstack/react-query'
import { BookOpen, Search } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { knowledgeApi } from '../../api/knowledge'
import { PortalErrorState } from '../../layout/PortalShell'

/**
 * The self-service half of the knowledge base.
 *
 * It reads the same endpoint the service desk's own list does, and gets published articles only — narrowed
 * in the query rather than by this page asking for a narrower filter. That is the whole rule: `CanManageTickets`
 * deliberately includes EndUser so requesters can reach the portal, so nothing here may depend on a control
 * this page happens not to draw (WP-1.8).
 */
export function PortalKbPage() {
  const [search, setSearch] = useState('')

  const articles = useQuery({
    queryKey: ['portal-kb', search],
    queryFn: () => knowledgeApi.list({ search: search || undefined, pageSize: 25 }),
  })

  const items = articles.data?.items ?? []

  return <div className="space-y-8">
    <div>
      <h1 className="text-[32px] font-bold leading-tight">Help articles</h1>
      <p className="mt-2 text-base text-slate-500">
        Answers the service desk has already written down. Worth a look before you raise a request — many
        things are quicker to fix than to report.
      </p>
    </div>

    <label className="relative block">
      <span className="sr-only">Search help articles</span>
      <Search size={18} className="pointer-events-none absolute left-4 top-1/2 -translate-y-1/2 text-slate-400" />
      <input
        autoFocus
        className="input h-12 pl-11 text-base"
        placeholder="What do you need help with?"
        value={search}
        onChange={(event) => setSearch(event.target.value)}
      />
    </label>

    {articles.isPending
      ? <div aria-label="Loading articles" className="space-y-3">
          {[0, 1, 2].map((index) => <div key={index} className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />)}
        </div>
      : articles.isError
        ? <PortalErrorState
            title="The help articles could not be loaded"
            message={articles.error.message}
            retry={() => void articles.refetch()}
          />
        : items.length === 0
          ? <div className="rounded-xl border border-slate-200 bg-white p-12 text-center dark:border-slate-800 dark:bg-slate-900">
              <span className="mx-auto grid size-12 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800"><BookOpen size={22} /></span>
              <h2 className="mt-4 text-lg font-semibold">
                {search ? 'Nothing here matches that' : 'No articles yet'}
              </h2>
              <p className="mx-auto mt-2 max-w-md text-sm text-slate-500">
                {search
                  ? 'Try a different word — or raise a request and somebody will answer it directly.'
                  : 'The service desk has not published anything yet. Raise a request and somebody will help.'}
              </p>
              <Link to="/portal/new" className="mt-5 inline-flex h-11 items-center rounded-lg bg-blue-600 px-4 text-sm font-medium text-white hover:bg-blue-700">
                New request
              </Link>
            </div>
          : <ul className="space-y-3">
              {items.map((article) => <li key={article.id}>
                <Link
                  to={`/portal/kb/${article.id}`}
                  className="block rounded-xl border border-slate-200 bg-white p-5 transition-colors hover:border-blue-300 dark:border-slate-800 dark:bg-slate-900">
                  <h2 className="text-lg font-semibold">{article.title}</h2>
                  <p className="mt-1 text-sm text-slate-500">{article.summary}</p>
                  <p className="mt-3 text-xs text-slate-400">{article.categoryName ?? 'General'}</p>
                </Link>
              </li>)}
            </ul>}
  </div>
}
