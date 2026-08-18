import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BookOpen, FileText, Globe, Plus, Search } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { knowledgeApi, type KbArticleStatus, type CreateKbArticleInput } from '../../api/knowledge'
import { helpdeskApi } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { formatLocal } from '../tickets/ticketUi'
import { KbStatusPill, kbStatuses } from './knowledgeUi'

/**
 * The knowledge base as the service desk sees it: every article whatever its state, because the person
 * who has to finish a draft is the one who needs to find it.
 *
 * The portal reads the same endpoint and gets published articles only — narrowed in the query rather than
 * by this screen offering a different filter, which is the rule that makes the two views safe to share.
 */
export function KbListPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<KbArticleStatus | ''>('')
  const [creating, setCreating] = useState(false)

  const articles = useQuery({
    queryKey: ['kb-articles', { search, status }],
    queryFn: () => knowledgeApi.list({
      search: search || undefined,
      statuses: status ? [status] : undefined,
      pageSize: 100,
    }),
  })

  const items = articles.data?.items ?? []
  const publishedCount = items.filter((article) => article.status === 'Published').length
  const draftCount = items.filter((article) => article.status === 'Draft').length

  return <div className="space-y-6">
    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[28px] font-bold">Knowledge</h1>
        <p className="mt-1 text-sm text-slate-500">
          The answers the service desk has already written down — offered while a ticket is being typed, and
          searchable from the portal once they are published.
        </p>
      </div>
      <div className="sm:ml-auto">
        <Button onClick={() => setCreating(true)}><Plus size={18} />New article</Button>
      </div>
    </div>

    <div className="grid gap-4 sm:grid-cols-3">
      <Kpi label="Articles shown" value={articles.data?.total} tone="text-blue-600 bg-blue-50 dark:bg-blue-500/15" icon={<BookOpen size={20} />} />
      <Kpi label="Published here" value={articles.isSuccess ? publishedCount : undefined} tone="text-green-600 bg-green-50 dark:bg-green-500/15" icon={<Globe size={20} />} />
      <Kpi label="Still drafts" value={articles.isSuccess ? draftCount : undefined} tone="text-amber-600 bg-amber-50 dark:bg-amber-500/15" icon={<FileText size={20} />} />
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="relative min-w-56 flex-1">
          <span className="sr-only">Search knowledge</span>
          <Search size={16} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
          <input
            className="input pl-9"
            placeholder="Search titles, summaries, keywords and bodies"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
        </label>
        <select aria-label="Filter by status" className="input w-auto min-w-44" value={status} onChange={(event) => setStatus(event.target.value as KbArticleStatus | '')}>
          <option value="">Every status</option>
          {kbStatuses.map((option) => <option key={option} value={option}>{option}</option>)}
        </select>
      </div>

      {articles.isPending ? <TableSkeleton />
        : articles.isError ? <ErrorState message={articles.error.message} retry={() => void articles.refetch()} />
        : items.length === 0 ? <EmptyState filtered={Boolean(search || status)} onCreate={() => setCreating(true)} />
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[900px] text-left text-sm">
              <thead><tr>
                {['Article', 'Category', 'Status', 'Version', 'On tickets', 'Updated'].map((header) =>
                  <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {items.map((article) => <tr key={article.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4">
                    <Link to={`/knowledge/${article.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{article.title}</Link>
                    <span className="ml-2 text-xs text-slate-500">{article.number}</span>
                    <p className="mt-0.5 max-w-xl truncate text-xs text-slate-500">{article.summary}</p>
                  </td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{article.categoryName ?? 'Uncategorised'}</td>
                  <td className="h-12 px-4"><KbStatusPill status={article.status} /></td>
                  <td className="h-12 px-4 text-right tabular-nums text-slate-600 dark:text-slate-300">v{article.version}</td>
                  <td className="h-12 px-4 text-right tabular-nums text-slate-600 dark:text-slate-300">{article.linkedTicketCount}</td>
                  <td className="h-12 px-4 text-slate-500">{formatLocal(article.updatedAt)}</td>
                </tr>)}
              </tbody>
            </table>
          </div>}
    </section>

    {creating && <CreateArticleDialog
      onClose={() => setCreating(false)}
      onCreated={async (id) => {
        setCreating(false)
        await queryClient.invalidateQueries({ queryKey: ['kb-articles'] })
        navigate(`/knowledge/${id}`)
      }}
    />}
  </div>
}

/**
 * A new article is always a draft — publishing is a separate act with an entry condition — so this dialog
 * asks for what an article needs and nothing about its state.
 */
function CreateArticleDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => Promise<void> }) {
  const [title, setTitle] = useState('')
  const [summary, setSummary] = useState('')
  const [body, setBody] = useState('')
  const [keywords, setKeywords] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const categories = useQuery({ queryKey: ['ticket-categories'], queryFn: helpdeskApi.listCategories, retry: false, meta: { suppressErrorToast: true } })

  const create = useMutation({
    mutationFn: (input: CreateKbArticleInput) => knowledgeApi.create(input),
    onSuccess: async (article) => {
      toast.success(`${article.number} created as a draft`)
      await onCreated(article.id)
    },
  })

  const flat = flatten(categories.data ?? [])

  return <div className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section role="dialog" aria-modal="true" aria-labelledby="create-article-title" className="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 id="create-article-title" className="text-lg font-semibold">New article</h2>
      <p className="mt-1 text-sm text-slate-500">
        It starts as a draft. Nobody outside the service desk sees it, and nothing suggests it, until it is
        published.
      </p>
      <form className="mt-5 space-y-4" onSubmit={(event) => {
        event.preventDefault()
        create.mutate({
          title,
          summary,
          body,
          keywords: keywords || null,
          categoryId: categoryId || null,
        })
      }}>
        <Field id="kb-title" label="Title">
          <input id="kb-title" autoFocus className="input" value={title} onChange={(event) => setTitle(event.target.value)} />
        </Field>
        <Field id="kb-summary" label="Summary" hint="One or two lines. This is what a requester reads before they open it.">
          <input id="kb-summary" aria-describedby="kb-summary-hint" className="input" value={summary} onChange={(event) => setSummary(event.target.value)} />
        </Field>
        <Field id="kb-body" label="Body" hint="Markdown: ## headings, - bullets, **bold**.">
          <textarea id="kb-body" aria-describedby="kb-body-hint" rows={10} className="input resize-y font-mono text-[13px]" value={body} onChange={(event) => setBody(event.target.value)} />
        </Field>
        <Field id="kb-keywords" label="Keywords" hint="What people call it that the article does not say. Comma separated.">
          <input id="kb-keywords" aria-describedby="kb-keywords-hint" className="input" value={keywords} onChange={(event) => setKeywords(event.target.value)} />
        </Field>
        <Field id="kb-category" label="Category">
          <select id="kb-category" className="input" value={categoryId} onChange={(event) => setCategoryId(event.target.value)}>
            <option value="">Uncategorised</option>
            {flat.map((category) => <option key={category.id} value={category.id}>{category.label}</option>)}
          </select>
        </Field>
        {create.error && <p role="alert" className="text-sm text-red-600">{create.error.message}</p>}
        <div className="flex justify-end gap-3">
          <Button type="button" variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" disabled={create.isPending}>{create.isPending ? 'Creating…' : 'Create draft'}</Button>
        </div>
      </form>
    </section>
  </div>
}

type CategoryNode = { id: string; name: string; children?: CategoryNode[] }

/** The category tree as one indented list, because a select cannot nest. */
function flatten(nodes: CategoryNode[], prefix = ''): { id: string; label: string }[] {
  return nodes.flatMap((node) => [
    { id: node.id, label: `${prefix}${node.name}` },
    ...flatten(node.children ?? [], `${prefix}${node.name} / `),
  ])
}

/**
 * The hint sits **outside** the `<label>` element and is tied to the field with `aria-describedby`, which
 * is why the label carries `htmlFor` rather than wrapping the control.
 *
 * A hint inside a label becomes part of the field's accessible name — a screen reader then reads the whole
 * sentence as the field's name, and `getByLabelText('Summary')` finds nothing. That is the defect WP-5.7
 * found and left a standing note about, and it is what this shape avoids.
 */
function Field({ id, label, hint, children }: { id: string; label: string; hint?: string; children: ReactNode }) {
  return <div>
    <label htmlFor={id} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">{label}</label>
    {hint && <p id={`${id}-hint`} className="mb-1.5 text-xs text-slate-500">{hint}</p>}
    {children}
  </div>
}

function Kpi({ label, value, tone, icon }: { label: string; value?: number; tone: string; icon: ReactNode }) {
  return <div className="flex items-center gap-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <span className={`grid size-10 place-items-center rounded-full ${tone}`}>{icon}</span>
    <div>
      <p className="text-[13px] text-slate-500">{label}</p>
      {value === undefined
        ? <div className="mt-1 h-7 w-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />
        : <p className="text-[30px] font-bold leading-8 tabular-nums">{value}</p>}
    </div>
  </div>
}

function TableSkeleton() {
  return <div aria-label="Loading articles" className="space-y-2 p-4">
    {[0, 1, 2, 3].map((index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
  </div>
}

function ErrorState({ message, retry }: { message: string; retry: () => void }) {
  return <div role="alert" className="p-10 text-center">
    <h3 className="font-semibold">The knowledge base could not be read</h3>
    <p className="mx-auto mt-1 max-w-md text-sm text-slate-500">{message}</p>
    <Button className="mt-4" variant="secondary" onClick={retry}>Try again</Button>
  </div>
}

/** Never a bare "No data" (DESIGN §6) — and the two empty states are different facts. */
function EmptyState({ filtered, onCreate }: { filtered: boolean; onCreate: () => void }) {
  return <div className="p-12 text-center">
    <span className="mx-auto grid size-12 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800"><BookOpen size={22} /></span>
    <h3 className="mt-4 font-semibold">{filtered ? 'Nothing matches that' : 'Nothing has been written down yet'}</h3>
    <p className="mx-auto mt-1 max-w-md text-sm text-slate-500">
      {filtered
        ? 'Try a different word, or clear the status filter — drafts and archived articles are hidden by it.'
        : 'The first article is usually the answer you have already given three times this month.'}
    </p>
    {!filtered && <Button className="mt-4" onClick={onCreate}><Plus size={18} />New article</Button>}
  </div>
}
