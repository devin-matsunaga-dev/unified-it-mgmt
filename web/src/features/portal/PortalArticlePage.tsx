import { useQuery } from '@tanstack/react-query'
import { ArrowLeft } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { knowledgeApi } from '../../api/knowledge'
import { ArticleBody } from '../knowledge/knowledgeUi'
import { PortalErrorState } from '../../layout/PortalShell'

/**
 * One article, read by the person it was written for.
 *
 * Deliberately lighter than the agent's view (DESIGN §9): no status pill, no version, no history — those
 * are the service desk's business. A draft asked for by id is a 404 from the server, which is what the
 * error state here says, because "you may not read this one" would confirm that a draft about their
 * question exists.
 */
export function PortalArticlePage() {
  const { id = '' } = useParams()
  const article = useQuery({
    queryKey: ['portal-kb-article', id],
    queryFn: () => knowledgeApi.get(id),
    enabled: Boolean(id),
    retry: false,
  })

  return <div className="space-y-8">
    <Link to="/portal/kb" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600">
      <ArrowLeft size={17} />Back to help articles
    </Link>

    {article.isPending
      ? <div aria-label="Loading article" className="h-64 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
      : article.isError
        ? <PortalErrorState
            title="That article is not available"
            message="It may have been withdrawn, or it may never have been published. The help article list has everything that is."
            retry={() => void article.refetch()}
          />
        : <article className="rounded-xl border border-slate-200 bg-white p-8 dark:border-slate-800 dark:bg-slate-900">
            <h1 className="text-[28px] font-bold leading-tight">{article.data.title}</h1>
            <p className="mt-2 text-base text-slate-500">{article.data.summary}</p>
            <hr className="my-6 border-slate-200 dark:border-slate-800" />
            <ArticleBody className="text-base" body={article.data.body} />
          </article>}

    <div className="rounded-xl border border-slate-200 bg-white p-6 text-center dark:border-slate-800 dark:bg-slate-900">
      <p className="text-sm text-slate-500">Still stuck? Somebody at the service desk will pick it up.</p>
      <Link to="/portal/new" className="mt-3 inline-flex h-11 items-center rounded-lg bg-blue-600 px-4 text-sm font-medium text-white hover:bg-blue-700">
        Raise a request
      </Link>
    </div>
  </div>
}
