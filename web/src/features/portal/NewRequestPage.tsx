import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, BookOpen, ChevronDown, ExternalLink, Send } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useForm } from 'react-hook-form'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { z } from 'zod'
import { helpdeskApi, type CreateTicketInput } from '../../api/helpdesk'
import { knowledgeApi, type KbSuggestion } from '../../api/knowledge'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { ArticleBody } from '../knowledge/knowledgeUi'
import { useKbSuggestions } from '../knowledge/KbSuggestions'
import { CategorySelect, CustomFieldInputs } from '../tickets/CategoryFields'
import { customFieldPayload, findCategory, validateCustomFields } from '../tickets/categoryFields'

const defaultQueueName = 'Service Desk'

const schema = z.object({
  title: z.string().trim().min(3, 'Enter at least 3 characters.').max(200, 'Keep the summary under 200 characters.'),
  description: z.string().trim().min(1, 'Tell us what is happening.').max(10_000),
  type: z.enum(['Incident', 'ServiceRequest']),
  urgency: z.enum(['Low', 'Medium', 'High']),
})
type FormInput = z.input<typeof schema>
type FormValues = z.output<typeof schema>

export function NewRequestPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const queues = useQuery({ queryKey: ['queues'], queryFn: helpdeskApi.listQueues, retry: false, meta: { suppressErrorToast: true } })
  // Always refetched on mount: a cached tree can miss a field an admin has since made required,
  // which would submit an incomplete request and get a 400 the form cannot attribute to a field.
  const categories = useQuery({ queryKey: ['ticket-categories'], queryFn: helpdeskApi.listCategories, staleTime: 0, refetchOnMount: 'always', retry: false, meta: { suppressErrorToast: true } })
  const [categoryId, setCategoryId] = useState('')
  const [customFields, setCustomFields] = useState<Record<string, string>>({})
  const [categoryErrors, setCategoryErrors] = useState<Record<string, string>>({})
  // The deflection prompt has been shown and answered. One press of Submit shows it; the next one sends the
  // request. It is a prompt and never a gate: nobody is prevented from raising anything.
  const [deflectionAnswered, setDeflectionAnswered] = useState(false)
  const { register, handleSubmit, watch, formState: { errors } } = useForm<FormInput, unknown, FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { type: 'Incident', urgency: 'Medium' },
  })
  const create = useMutation({
    mutationFn: (input: CreateTicketInput) => helpdeskApi.createTicket(input),
    onSuccess: async (request) => {
      await queryClient.invalidateQueries({ queryKey: ['tickets'] })
      toast.success(`Request ${request.number} submitted`)
      navigate(`/portal/requests/${request.id}`)
    },
  })
  const tree = useMemo(() => categories.data ?? [], [categories.data])
  const category = useMemo(() => findCategory(tree, categoryId), [tree, categoryId])
  const fields = category?.fields ?? []
  // WP-5.9's deflection read, asked from what has been typed so far. Published articles only, which is
  // enforced by the endpoint rather than by this page — the portal cannot ask for anything else.
  const suggestions = useKbSuggestions({
    subject: watch('title') ?? '',
    body: watch('description') ?? '',
    categoryId: categoryId || null,
  })
  const deflections = suggestions.data ?? []
  const submit = handleSubmit((values) => {
    const fieldErrors = validateCustomFields(fields, customFields)
    if (tree.length > 0 && !categoryId) fieldErrors.category = 'Choose what this request is about.'
    setCategoryErrors(fieldErrors)
    if (Object.keys(fieldErrors).length > 0) return
    // The prompt, once, and only when there is something to read. Answered by pressing Submit again — a
    // request nobody can raise until they have clicked through an article is a portal people stop using.
    if (deflections.length > 0 && !deflectionAnswered) {
      setDeflectionAnswered(true)
      return
    }

    const queue = queues.data?.find((item) => item.name === defaultQueueName) ?? queues.data?.[0]
    create.mutate({
      ...values,
      impact: 'Medium',
      requesterId: null,
      queueId: queue?.id ?? null,
      categoryId: categoryId || null,
      customFields: customFieldPayload(fields, customFields),
    })
  })

  return <div className="space-y-8">
    <Link to="/portal" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to my requests</Link>
    <div>
      <h1 className="text-[32px] font-bold leading-tight">New request</h1>
      <p className="mt-2 text-base text-slate-500">Tell us what you need. The service desk picks requests up in the order they arrive.</p>
    </div>

    <form onSubmit={(event) => void submit(event)} className="space-y-6">
      <fieldset className="space-y-5 rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
        <legend className="px-1 text-base font-semibold">What is this request about?</legend>
        <div>
          <label htmlFor="ticket-category" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-200">Category</label>
          {categories.isLoading
            ? <div aria-label="Loading categories" className="h-11 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />
            : tree.length === 0
              ? <p className="text-sm text-slate-500">No categories have been set up yet — the service desk will categorise this request for you.</p>
              : <CategorySelect categories={tree} value={categoryId} onChange={(value) => { setCategoryId(value); setCustomFields({}); setCategoryErrors({}) }} error={categoryErrors.category} placeholder="Choose a category" />}
        </div>
        <CustomFieldInputs fields={fields} values={customFields} errors={categoryErrors} onChange={(key, value) => setCustomFields((current) => ({ ...current, [key]: value }))} />
      </fieldset>

      <div className="space-y-5 rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
        <PortalField label="Short summary" hint="One line the service desk will see first." error={errors.title?.message}>
          <input className="input h-11" placeholder="e.g. Laptop will not connect to the VPN" {...register('title')} />
        </PortalField>
        <PortalField label="What is happening?" hint="Include what you were doing, any error message, and when it started." error={errors.description?.message}>
          <textarea rows={7} className="input resize-y" {...register('description')} />
        </PortalField>
        <PortalField label="Is something broken, or do you need something new?" error={errors.type?.message}>
          <select className="input h-11 sm:max-w-xs" {...register('type')}>
            <option value="Incident">Something is broken</option>
            <option value="ServiceRequest">I need something new</option>
          </select>
        </PortalField>
        <PortalField label="How urgent is this for you?" error={errors.urgency?.message}>
          <select className="input h-11 sm:max-w-xs" {...register('urgency')}>
            <option value="Low">Low — I can work around it</option>
            <option value="Medium">Medium — it is slowing me down</option>
            <option value="High">High — I cannot work</option>
          </select>
        </PortalField>
      </div>

      {deflections.length > 0 && <DeflectionPrompt suggestions={deflections} answered={deflectionAnswered} />}

      {create.error && <p role="alert" className="text-sm text-red-600">{create.error.message}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button type="button" variant="secondary" className="h-11" onClick={() => navigate('/portal')}>Cancel</Button>
        <Button type="submit" className="h-11" disabled={create.isPending}>
          <Send size={17} />
          {create.isPending
            ? 'Submitting…'
            : deflections.length > 0 && deflectionAnswered
              ? 'Submit anyway'
              : 'Submit request'}
        </Button>
      </div>
    </form>
  </div>
}

/**
 * The deflection prompt: articles that look like what somebody has just described, offered before the
 * request goes anywhere.
 *
 * Two states, and the difference matters. Before Submit is pressed it is an aside — "this may already be
 * answered" — so it cannot interrupt somebody who knows exactly what they want. After Submit is pressed
 * once it says so out loud, and the button changes to "Submit anyway", which is the moment the prompt is
 * actually for. Articles expand in place rather than navigating, because a half-typed request lost to a
 * link is worse than no prompt at all.
 */
function DeflectionPrompt({ suggestions, answered }: { suggestions: KbSuggestion[]; answered: boolean }) {
  const [openId, setOpenId] = useState<string | null>(null)

  return <section
    aria-live="polite"
    className={cn(
      'rounded-xl border p-6',
      answered
        ? 'border-blue-300 bg-blue-50 dark:border-blue-800 dark:bg-blue-950/40'
        : 'border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900',
    )}>
    <div className="flex items-start gap-3">
      <span className="grid size-10 shrink-0 place-items-center rounded-full bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-300"><BookOpen size={20} /></span>
      <div>
        <h2 className="text-base font-semibold">
          {answered ? 'Before you send this — one of these may answer it' : 'This may already be answered'}
        </h2>
        <p className="mt-1 text-sm text-slate-500">
          {answered
            ? 'Have a look. If none of them help, press Submit anyway and somebody will pick it up.'
            : 'Open one to read it here. Your request keeps everything you have typed.'}
        </p>
      </div>
    </div>

    <ul className="mt-4 space-y-2">
      {suggestions.map((suggestion) => <li key={suggestion.id} className="rounded-lg border border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-900">
        <button
          type="button"
          aria-expanded={openId === suggestion.id}
          className="flex w-full items-center gap-3 p-4 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          onClick={() => setOpenId((current) => (current === suggestion.id ? null : suggestion.id))}>
          <span className="min-w-0 flex-1">
            <span className="block font-medium">{suggestion.title}</span>
            <span className="mt-0.5 block text-sm text-slate-500">{suggestion.summary}</span>
          </span>
          <ChevronDown size={18} className={cn('shrink-0 text-slate-400 transition-transform', openId === suggestion.id && 'rotate-180')} />
        </button>
        {openId === suggestion.id && <ArticleContent id={suggestion.id} />}
      </li>)}
    </ul>
  </section>
}

/** Fetched only when it is opened — a prompt that pre-loaded five whole articles would be five reads nobody asked for. */
function ArticleContent({ id }: { id: string }) {
  const article = useQuery({
    queryKey: ['portal-kb-article', id],
    queryFn: () => knowledgeApi.get(id),
    retry: false,
    meta: { suppressErrorToast: true },
  })

  if (article.isPending) return <div aria-label="Loading article" className="m-4 h-24 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />
  if (article.isError) return <p className="border-t border-slate-200 p-4 text-sm text-slate-500 dark:border-slate-700">
    This article could not be opened. <Link className="text-blue-600 hover:underline" to={`/portal/kb/${id}`}>Try it on its own page</Link>.
  </p>

  return <div className="border-t border-slate-200 p-4 dark:border-slate-700">
    <ArticleBody body={article.data.body} />
    <Link
      to={`/portal/kb/${id}`}
      target="_blank"
      rel="noreferrer"
      className="mt-3 inline-flex items-center gap-1.5 text-[13px] font-medium text-blue-600 hover:underline">
      Open in a new tab<ExternalLink size={14} />
    </Link>
  </div>
}

function PortalField({ label, hint, error, children }: { label: string; hint?: string; error?: string; children: React.ReactNode }) {
  return <label className="block">
    <span className="block text-sm font-medium text-slate-700 dark:text-slate-200">{label}</span>
    {hint && <span className="mb-2 mt-0.5 block text-[13px] text-slate-500">{hint}</span>}
    <span className={cn('block', !hint && 'mt-2')}>{children}</span>
    {error && <span className="mt-1.5 block text-xs text-red-600">{error}</span>}
  </label>
}
