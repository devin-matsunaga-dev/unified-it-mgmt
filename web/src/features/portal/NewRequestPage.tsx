import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Laptop, Send, Sparkles } from 'lucide-react'
import { useForm } from 'react-hook-form'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { z } from 'zod'
import { helpdeskApi, type CreateTicketInput } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'

const defaultQueueName = 'Service Desk'
const requestTypes = [
  { value: 'Incident', label: 'Something is broken', hint: 'A device, app, or service stopped working the way it should.', icon: Laptop },
  { value: 'ServiceRequest', label: 'I need something new', hint: 'Access, software, hardware, or a change you would like set up.', icon: Sparkles },
] as const

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
  const selectedType = watch('type')
  const submit = handleSubmit((values) => {
    const queue = queues.data?.find((item) => item.name === defaultQueueName) ?? queues.data?.[0]
    create.mutate({ ...values, impact: 'Medium', requesterId: null, queueId: queue?.id ?? null })
  })

  return <div className="space-y-8">
    <Link to="/portal" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to my requests</Link>
    <div>
      <h1 className="text-[32px] font-bold leading-tight">New request</h1>
      <p className="mt-2 text-base text-slate-500">Tell us what you need. The service desk picks requests up in the order they arrive.</p>
    </div>

    <form onSubmit={(event) => void submit(event)} className="space-y-6">
      <fieldset className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
        <legend className="px-1 text-base font-semibold">What kind of request is this?</legend>
        <div className="mt-4 grid gap-3 sm:grid-cols-2">
          {requestTypes.map(({ value, label, hint, icon: Icon }) => <label key={value} className={cn('flex cursor-pointer gap-3 rounded-xl border p-4 transition-colors', selectedType === value ? 'border-blue-600 bg-blue-50 dark:bg-blue-950/40' : 'border-slate-200 hover:border-slate-300 dark:border-slate-700')}>
            <input type="radio" value={value} className="mt-1" {...register('type')} />
            <span>
              <span className="flex items-center gap-2 text-sm font-semibold"><Icon size={18} />{label}</span>
              <span className="mt-1 block text-sm text-slate-500">{hint}</span>
            </span>
          </label>)}
        </div>
      </fieldset>

      <div className="space-y-5 rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
        <PortalField label="Short summary" hint="One line the service desk will see first." error={errors.title?.message}>
          <input className="input h-11" placeholder="e.g. Laptop will not connect to the VPN" {...register('title')} />
        </PortalField>
        <PortalField label="What is happening?" hint="Include what you were doing, any error message, and when it started." error={errors.description?.message}>
          <textarea rows={7} className="input resize-y" {...register('description')} />
        </PortalField>
        <PortalField label="How urgent is this for you?" error={errors.urgency?.message}>
          <select className="input h-11 sm:max-w-xs" {...register('urgency')}>
            <option value="Low">Low — I can work around it</option>
            <option value="Medium">Medium — it is slowing me down</option>
            <option value="High">High — I cannot work</option>
          </select>
        </PortalField>
      </div>

      {create.error && <p role="alert" className="text-sm text-red-600">{create.error.message}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button type="button" variant="secondary" className="h-11" onClick={() => navigate('/portal')}>Cancel</Button>
        <Button type="submit" className="h-11" disabled={create.isPending}><Send size={17} />{create.isPending ? 'Submitting…' : 'Submit request'}</Button>
      </div>
    </form>
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
