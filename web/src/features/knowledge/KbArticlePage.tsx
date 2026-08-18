import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, History, Pencil, RotateCcw, Save, ShieldQuestion, Trash2, X } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { knowledgeApi, type KbArticle, type KbArticleStatus } from '../../api/knowledge'
import { Button } from '../../components/ui/Button'
import { formatLocal } from '../tickets/ticketUi'
import { ArticleBody, KbStatusPill, kbTransitionLabel } from './knowledgeUi'
import { usePageHeading } from '../../layout/pageHeading'

/**
 * One article: what it says, what it used to say, and the two buttons that decide whether anybody outside
 * the service desk can read it.
 *
 * The workflow buttons are drawn from `nextStatuses` on the record rather than from a copy of the workflow
 * kept here — WP-5.8's call, taken because WP-5.7's own note records the failure mode of the alternative: a
 * button that is never offered, which nobody reports because nobody knew it should be there.
 */
export function KbArticlePage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [showHistory, setShowHistory] = useState(false)

  const article = useQuery({ queryKey: ['kb-article', id], queryFn: () => knowledgeApi.get(id), enabled: Boolean(id) })
  usePageHeading(article.data ? { title: article.data.title } : null)

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['kb-article', id] })
    await queryClient.invalidateQueries({ queryKey: ['kb-articles'] })
  }

  const transition = useMutation({
    mutationFn: (target: KbArticleStatus) => knowledgeApi.transition(id, target),
    onSuccess: async (updated) => {
      await refresh()
      toast.success(updated.status === 'Published'
        ? `${updated.number} is published — the portal can find it now`
        : `${updated.number} is now ${updated.status.toLowerCase()}`)
    },
  })

  const restore = useMutation({
    mutationFn: (version: number) => knowledgeApi.restore(id, version),
    onSuccess: async (updated) => {
      await refresh()
      toast.success(`Restored as version ${updated.version}`)
    },
  })

  const remove = useMutation({
    mutationFn: () => knowledgeApi.remove(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['kb-articles'] })
      toast.success('Article deleted')
      navigate('/knowledge')
    },
  })

  if (article.isPending) return <div aria-label="Loading article" className="h-64 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
  if (article.isError) return <div role="alert" className="rounded-xl border border-slate-200 bg-white p-10 text-center dark:border-slate-800 dark:bg-slate-900">
    <h2 className="text-lg font-semibold">This article could not be opened</h2>
    <p className="mx-auto mt-2 max-w-md text-sm text-slate-500">{article.error.message}</p>
    <Button className="mt-5" variant="secondary" onClick={() => void article.refetch()}>Try again</Button>
  </div>

  const item = article.data

  return <div className="space-y-6">
    <Link to="/knowledge" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to knowledge</Link>

    <div className="flex flex-col gap-4 xl:flex-row xl:items-start">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-[28px] font-bold">{item.title}</h1>
          <KbStatusPill status={item.status} />
        </div>
        <p className="mt-1 text-sm text-slate-500">
          {item.number} · version {item.version} · {item.categoryName ?? 'Uncategorised'} · updated {formatLocal(item.updatedAt)}
        </p>
      </div>
      <div className="flex flex-wrap gap-2">
        {!editing && <Button variant="secondary" onClick={() => setEditing(true)}><Pencil size={17} />Edit</Button>}
        {item.nextStatuses.map((target) => <Button
          key={target}
          variant={target === 'Published' ? 'primary' : 'secondary'}
          disabled={transition.isPending}
          onClick={() => transition.mutate(target)}>
          {kbTransitionLabel(target)}
        </Button>)}
      </div>
    </div>

    {transition.error && <p role="alert" className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 dark:border-red-900 dark:bg-red-950/40 dark:text-red-300">{transition.error.message}</p>}

    <div className="grid gap-6 xl:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
      <div className="space-y-6">
        {editing
          ? <ArticleEditor article={item} onCancel={() => setEditing(false)} onSaved={async () => { setEditing(false); await refresh() }} />
          : <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
              <p className="text-sm font-medium text-slate-600 dark:text-slate-300">{item.summary}</p>
              <hr className="my-4 border-slate-200 dark:border-slate-800" />
              <ArticleBody body={item.body} />
            </section>}

        <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
          <button
            className="flex w-full items-center gap-3 p-5 text-left"
            aria-expanded={showHistory}
            onClick={() => setShowHistory((current) => !current)}>
            <span className="grid size-10 place-items-center rounded-full bg-slate-100 text-slate-600 dark:bg-slate-800"><History size={20} /></span>
            <span>
              <span className="block font-semibold">Version history</span>
              <span className="mt-0.5 block text-sm text-slate-500">
                {(item.revisions?.length ?? 0) === 0
                  ? 'This is the first version — nothing has been replaced yet.'
                  : `${item.revisions!.length} earlier version${item.revisions!.length === 1 ? '' : 's'}, newest first.`}
              </span>
            </span>
          </button>
          {showHistory && (item.revisions?.length ?? 0) > 0 && <ul className="border-t border-slate-200 dark:border-slate-800">
            {item.revisions!.map((revision) => <li key={revision.version} className="border-b border-slate-100 p-5 last:border-b-0 dark:border-slate-800">
              <div className="flex flex-wrap items-baseline gap-2">
                <span className="font-medium">Version {revision.version}</span>
                <span className="text-xs text-slate-500">{revision.authorName} · replaced {formatLocal(revision.createdAt)}</span>
                <Button
                  className="ml-auto"
                  variant="secondary"
                  disabled={restore.isPending}
                  onClick={() => restore.mutate(revision.version)}>
                  <RotateCcw size={16} />Restore
                </Button>
              </div>
              <p className="mt-2 text-sm font-medium text-slate-600 dark:text-slate-300">{revision.title}</p>
              <ArticleBody className="mt-2 opacity-80" body={revision.body} />
            </li>)}
          </ul>}
          {restore.error && <p role="alert" className="border-t border-slate-200 p-4 text-sm text-red-600 dark:border-slate-800">{restore.error.message}</p>}
        </section>
      </div>

      <div className="space-y-6">
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">About this article</h2>
          <dl className="mt-4 space-y-3 text-sm">
            <Detail label="Written by" value={item.authorName} />
            <Detail label="Published" value={item.publishedAt ? `${formatLocal(item.publishedAt)}${item.publishedByName ? ` by ${item.publishedByName}` : ''}` : 'Never'} />
            <Detail label="Keywords" value={item.keywords ?? 'None'} />
            <Detail label="Attached to tickets" value={String(item.linkedTicketCount)} />
          </dl>
          {item.problemId && <p className="mt-4 flex items-start gap-2 rounded-lg bg-slate-50 p-3 text-[13px] text-slate-600 dark:bg-slate-800/60 dark:text-slate-300">
            <ShieldQuestion size={17} className="mt-0.5 shrink-0" />
            <span>Written from <Link className="font-medium text-blue-600 hover:underline" to={`/problems/${item.problemId}`}>{item.problemNumber ?? 'a problem'}</Link>.</span>
          </p>}
        </section>

        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Retiring it</h2>
          <p className="mt-1 text-sm text-slate-500">
            Archiving keeps it findable by the service desk and takes it out of the portal and out of
            suggestions. Deleting is only for an article nothing has been answered with.
          </p>
          <Button
            className="mt-4"
            variant="secondary"
            disabled={remove.isPending || item.linkedTicketCount > 0}
            onClick={() => remove.mutate()}>
            <Trash2 size={17} />Delete
          </Button>
          {item.linkedTicketCount > 0 && <p className="mt-2 text-xs text-slate-500">
            {item.linkedTicketCount} ticket{item.linkedTicketCount === 1 ? ' has' : 's have'} been answered with this
            article, so it cannot be deleted. Archive it instead.
          </p>}
          {remove.error && <p role="alert" className="mt-2 text-sm text-red-600">{remove.error.message}</p>}
        </section>
      </div>
    </div>
  </div>
}

/**
 * Editing writes a version. Nothing is saved until Save, and a save that changed nothing is not a version —
 * the server decides that, so this form does not have to guess.
 */
function ArticleEditor({ article, onCancel, onSaved }: { article: KbArticle; onCancel: () => void; onSaved: () => Promise<void> }) {
  const [title, setTitle] = useState(article.title)
  const [summary, setSummary] = useState(article.summary)
  const [body, setBody] = useState(article.body)
  const [keywords, setKeywords] = useState(article.keywords ?? '')

  // Re-seeded when the article changes underneath, which is what a restore does.
  useEffect(() => {
    setTitle(article.title)
    setSummary(article.summary)
    setBody(article.body)
    setKeywords(article.keywords ?? '')
  }, [article])

  const save = useMutation({
    mutationFn: () => knowledgeApi.update(article.id, {
      title,
      summary,
      body,
      keywords: keywords || null,
      categoryId: article.categoryId,
    }),
    onSuccess: async (updated) => {
      toast.success(`Saved as version ${updated.version}`)
      await onSaved()
    },
  })

  return <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-center">
      <h2 className="font-semibold">Editing</h2>
      <Button variant="ghost" className="ml-auto size-9 p-0" aria-label="Stop editing" onClick={onCancel}><X size={19} /></Button>
    </div>
    <form className="mt-4 space-y-4" onSubmit={(event) => { event.preventDefault(); save.mutate() }}>
      <label className="block">
        <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Title</span>
        <input className="input" value={title} onChange={(event) => setTitle(event.target.value)} />
      </label>
      <label className="block">
        <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Summary</span>
        <input className="input" value={summary} onChange={(event) => setSummary(event.target.value)} />
      </label>
      <label className="block">
        <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Body</span>
        <textarea rows={16} className="input resize-y font-mono text-[13px]" value={body} onChange={(event) => setBody(event.target.value)} />
      </label>
      <label className="block">
        <span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Keywords</span>
        <input className="input" value={keywords} onChange={(event) => setKeywords(event.target.value)} />
      </label>
      {save.error && <p role="alert" className="text-sm text-red-600">{save.error.message}</p>}
      <div className="flex justify-end gap-3">
        <Button type="button" variant="secondary" onClick={onCancel}>Cancel</Button>
        <Button type="submit" disabled={save.isPending}><Save size={17} />{save.isPending ? 'Saving…' : 'Save version'}</Button>
      </div>
    </form>
  </section>
}

function Detail({ label, value }: { label: string; value: string }): ReactNode {
  return <div className="flex gap-3">
    <dt className="w-36 shrink-0 text-slate-500">{label}</dt>
    <dd className="min-w-0 flex-1 text-slate-700 dark:text-slate-200">{value}</dd>
  </div>
}
