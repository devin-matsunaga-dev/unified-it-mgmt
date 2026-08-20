import { useMutation, useQuery } from '@tanstack/react-query'
import { ChevronLeft, TicketPlus } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi } from '../../api/assets'
import { helpdeskApi, type TicketLevel } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { FieldActionBar } from '../../layout/FieldShell'
import { cn } from '../../lib/utils'

/**
 * Raising a ticket about the asset in your hand. Deliberately the shortest form the API will accept:
 * a title, what is wrong, and how urgent. Type, impact, queue, category and custom fields all take
 * their defaults — a technician standing at a broken printer is not the person to be classifying it,
 * and every one of those is editable later on the desktop by whoever picks the ticket up.
 */
const urgencies: { value: TicketLevel; label: string }[] = [
  { value: 'Low', label: 'Low' },
  { value: 'Medium', label: 'Medium' },
  { value: 'High', label: 'High' },
]

export function FieldTicketPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [urgency, setUrgency] = useState<TicketLevel>('Medium')

  const ci = useQuery({ queryKey: ['ci', id], queryFn: () => assetsApi.getCi(id), enabled: Boolean(id) })

  const create = useMutation({
    // One call, with the CI linked server-side inside the same transaction. Creating and then linking
    // would leave a ticket naming no asset whenever the second request is the one that drops, which
    // on the connection a plant room has is not a rare case.
    mutationFn: () => helpdeskApi.createTicket({
      title: title.trim(),
      description: description.trim(),
      type: 'Incident',
      urgency,
      impact: 'Medium',
      requesterId: null,
      queueId: null,
      categoryId: null,
      customFields: {},
      ciIds: [id],
    }),
    onSuccess: (ticket) => {
      toast.success(`${ticket.number} raised for ${ci.data?.name ?? 'this asset'}`)
      navigate(`/field/ci/${id}`, { replace: true })
    },
    onError: (error: Error) => toast.error(error.message),
  })

  return <>
    <Link to={`/field/ci/${id}`} className="inline-flex h-11 items-center gap-1 text-[15px] font-medium text-blue-600">
      <ChevronLeft size={18} />Back
    </Link>
    <h1 className="mt-1 text-[22px] font-bold leading-tight">Open a ticket</h1>
    <p className="mt-1 text-[15px] text-slate-500">
      {ci.data ? `Linked to ${ci.data.name}` : 'Linked to the asset you scanned'}
    </p>

    <form
      className="mt-5 space-y-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (title.trim() && description.trim()) create.mutate()
      }}
    >
      <div>
        <label htmlFor="field-ticket-title" className="text-[13px] font-medium text-slate-500">What is wrong?</label>
        <input
          id="field-ticket-title"
          value={title}
          onChange={(event) => setTitle(event.target.value)}
          maxLength={200}
          // 16px, or iOS Safari zooms the page on focus and the technician pinches back out one-handed.
          className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
        />
      </div>

      <div>
        <label htmlFor="field-ticket-description" className="text-[13px] font-medium text-slate-500">What did you see?</label>
        <textarea
          id="field-ticket-description"
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          rows={5}
          className="mt-1.5 w-full rounded-lg border border-slate-200 bg-white p-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
        />
      </div>

      <fieldset>
        <legend className="text-[13px] font-medium text-slate-500">Urgency</legend>
        <div className="mt-1.5 grid grid-cols-3 gap-2">
          {urgencies.map((option) => <button
            key={option.value}
            type="button"
            aria-pressed={urgency === option.value}
            onClick={() => setUrgency(option.value)}
            className={cn(
              'h-12 rounded-lg border text-[15px] font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
              urgency === option.value
                ? 'border-blue-600 bg-blue-50 text-blue-700 dark:bg-blue-950 dark:text-blue-300'
                : 'border-slate-200 bg-white text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300',
            )}
          >{option.label}</button>)}
        </div>
      </fieldset>

      <FieldActionBar>
        <Button type="submit" className="h-12 w-full text-[15px]" disabled={!title.trim() || !description.trim() || create.isPending}>
          <TicketPlus size={18} />{create.isPending ? 'Raising…' : 'Raise ticket'}
        </Button>
      </FieldActionBar>
    </form>
  </>
}
