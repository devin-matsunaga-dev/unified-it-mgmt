import { zodResolver } from '@hookform/resolvers/zod'
import { X } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { Button } from '../../components/ui/Button'
import type { CreateTicketInput, TicketCategory, TicketQueue } from '../../api/helpdesk'
import { CategorySelect, CustomFieldInputs } from './CategoryFields'
import { customFieldPayload, findCategory, validateCustomFields } from './categoryFields'

const schema = z.object({
  title: z.string().trim().min(3, 'Enter at least 3 characters.').max(200),
  description: z.string().trim().min(1, 'Describe the issue.').max(10_000),
  type: z.enum(['Incident', 'ServiceRequest']),
  urgency: z.enum(['Low', 'Medium', 'High']),
  impact: z.enum(['Low', 'Medium', 'High']),
  requesterId: z.string().trim().optional(),
  queueId: z.string().optional(),
})
type FormInput = z.input<typeof schema>
type FormValues = z.output<typeof schema>

export function QuickCreateTicket({ open, pending, error, queues, categories, onClose, onSubmit }: { open: boolean; pending: boolean; error?: string; queues: TicketQueue[]; categories: TicketCategory[]; onClose: () => void; onSubmit: (input: CreateTicketInput) => Promise<void> }) {
  const { register, handleSubmit, reset, formState: { errors } } = useForm<FormInput, unknown, FormValues>({ resolver: zodResolver(schema), defaultValues: { type: 'Incident', urgency: 'Medium', impact: 'Medium' } })
  const [categoryId, setCategoryId] = useState('')
  const [customFields, setCustomFields] = useState<Record<string, string>>({})
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const category = useMemo(() => findCategory(categories, categoryId), [categories, categoryId])
  const fields = category?.fields ?? []
  if (!open) return null
  const submit = handleSubmit(async (values) => {
    const invalid = validateCustomFields(fields, customFields)
    setFieldErrors(invalid)
    if (Object.keys(invalid).length > 0) return
    await onSubmit({ ...values, requesterId: values.requesterId || null, queueId: values.queueId || null, categoryId: categoryId || null, customFields: customFieldPayload(fields, customFields) })
    reset()
    setCategoryId('')
    setCustomFields({})
  })
  return <div className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section role="dialog" aria-modal="true" aria-labelledby="create-ticket-title" className="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-5 flex items-center"><div><h2 id="create-ticket-title" className="text-lg font-semibold">Quick-create ticket</h2><p className="mt-1 text-sm text-slate-500">Capture the essentials now; refine the ticket from its detail page.</p></div><Button variant="ghost" className="ml-auto size-9 p-0" aria-label="Close" onClick={onClose}><X size={19} /></Button></div>
      <form onSubmit={(event) => void submit(event)} className="grid gap-4 sm:grid-cols-2">
        <Field label="Title" error={errors.title?.message} className="sm:col-span-2"><input autoFocus className="input" {...register('title')} /></Field>
        <Field label="Description" error={errors.description?.message} className="sm:col-span-2"><textarea rows={4} className="input resize-y" {...register('description')} /></Field>
        <Field label="Type"><select className="input" {...register('type')}><option value="Incident">Incident</option><option value="ServiceRequest">Service request</option></select></Field>
        <Field label="Requester ID" error={errors.requesterId?.message}><input className="input" placeholder="Defaults to you" {...register('requesterId')} /></Field>
        <Field label="Queue"><select className="input" {...register('queueId')}><option value="">Unqueued</option>{queues.map((queue) => <option key={queue.id} value={queue.id}>{queue.name}</option>)}</select></Field>
        <div><label htmlFor="ticket-category" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Category</label><CategorySelect categories={categories} value={categoryId} onChange={(value) => { setCategoryId(value); setCustomFields({}); setFieldErrors({}) }} placeholder="Uncategorised" /></div>
        <Field label="Urgency"><select className="input" {...register('urgency')}><LevelOptions /></select></Field>
        <Field label="Impact"><select className="input" {...register('impact')}><LevelOptions /></select></Field>
        {fields.length > 0 && <div className="grid gap-4 rounded-lg border border-slate-200 p-4 sm:col-span-2 dark:border-slate-700"><p className="text-[13px] font-medium text-slate-500">{category?.name} details</p><CustomFieldInputs fields={fields} values={customFields} errors={fieldErrors} onChange={(key, value) => setCustomFields((current) => ({ ...current, [key]: value }))} /></div>}
        {error && <p role="alert" className="sm:col-span-2 text-sm text-red-600">{error}</p>}
        <div className="flex justify-end gap-3 sm:col-span-2"><Button type="button" variant="secondary" onClick={onClose}>Cancel</Button><Button type="submit" disabled={pending}>{pending ? 'Creating…' : 'Create ticket'}</Button></div>
      </form>
    </section>
  </div>
}

function LevelOptions() { return <><option value="Low">Low</option><option value="Medium">Medium</option><option value="High">High</option></> }
function Field({ label, error, className, children }: { label: string; error?: string; className?: string; children: React.ReactNode }) { return <label className={className}><span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">{label}</span>{children}{error && <span className="mt-1 block text-xs text-red-600">{error}</span>}</label> }
