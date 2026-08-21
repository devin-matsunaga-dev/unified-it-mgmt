import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { contractsApi } from '../../api/contracts'
import { Button } from '../../components/ui/Button'
import { usePageHeading } from '../../layout/pageHeading'

/**
 * When renewal notices go out. One setting for the whole platform, read by the nightly job on every
 * run — so a change takes effect the next night rather than the next restart.
 *
 * Thresholds are days, because that is what a notice is keyed on and a month is not a fixed number
 * of days. They are *offered* in months, because "three months before it expires" is how somebody
 * decides this and "90 days" is how the platform has to store it.
 */
const presets = [
  { days: 7, label: 'A week' },
  { days: 14, label: 'Two weeks' },
  { days: 30, label: '1 month' },
  { days: 60, label: '2 months' },
  { days: 90, label: '3 months' },
  { days: 180, label: '6 months' },
  { days: 365, label: '1 year' },
]

/** Matches the server's own ceiling; anything more and the owner stops reading them. */
const maximumThresholds = 6

/** Also the server's. Past a handful of addresses, a distribution group is the right tool. */
const maximumRecipients = 5

/** The same loose shape the server checks: one @, something either side, a dot in the domain. */
const emailShape = /^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$/

export function describeThreshold(days: number): string {
  if (days === 0) return 'On the day it expires'
  const preset = presets.find((option) => option.days === days)
  if (preset) return `${preset.label} before`
  return `${days} days before`
}

export function RenewalRemindersPage() {
  usePageHeading({ title: 'Renewal reminders' })
  const queryClient = useQueryClient()
  const settings = useQuery({
    queryKey: ['contract-reminder-settings'],
    queryFn: contractsApi.getReminderSettings,
  })

  const [days, setDays] = useState<number[]>([])
  const [enabled, setEnabled] = useState(true)
  const [custom, setCustom] = useState('')
  const [recipients, setRecipients] = useState<string[]>([])
  const [recipient, setRecipient] = useState('')
  const [loaded, setLoaded] = useState(false)

  // Loaded once. Re-seeding on every refetch would discard an edit in progress the moment anything
  // else invalidated the query.
  useEffect(() => {
    if (!settings.data || loaded) return
    setDays(settings.data.thresholdDays)
    setEnabled(settings.data.enabled)
    setRecipients(settings.data.recipients)
    setLoaded(true)
  }, [settings.data, loaded])

  const save = useMutation({
    mutationFn: () => contractsApi.saveReminderSettings({ thresholdDays: days, enabled, recipients }),
    onSuccess: async (saved) => {
      setDays(saved.thresholdDays)
      setRecipients(saved.recipients)
      await queryClient.invalidateQueries({ queryKey: ['contract-reminder-settings'] })
      toast.success('Renewal reminders updated')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  function add(value: number) {
    if (days.includes(value) || days.length >= maximumThresholds) return
    // Widest first, matching the order the server stores and the job reads them in.
    setDays([...days, value].sort((left, right) => right - left))
  }

  function addRecipient() {
    const address = recipient.trim().toLowerCase()
    if (!emailShape.test(address) || recipients.includes(address)) return
    if (recipients.length >= maximumRecipients) return
    setRecipients([...recipients, address])
    setRecipient('')
  }

  const sorted = [...days].sort((left, right) => right - left)
  const full = days.length >= maximumThresholds
  const recipientsFull = recipients.length >= maximumRecipients
  const trimmedRecipient = recipient.trim().toLowerCase()
  // Only complained about once there is something to complain about, so the field is not red while
  // it is being typed into.
  const recipientInvalid = trimmedRecipient !== '' && !emailShape.test(trimmedRecipient)

  if (settings.isLoading) {
    return <div aria-label="Loading" className="h-40 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
  }

  return <div className="max-w-[720px] space-y-6">
    <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="font-semibold">When to send</h2>
      <p className="mt-1 text-sm text-slate-500">
        A notice goes out once at each point below. Something that passes several at once is only
        mailed for the nearest, so adding more does not mean more mail on one night.
      </p>

      {sorted.length === 0
        ? <p className="mt-4 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
            No reminders set, so nothing will be sent.
          </p>
        : <ul className="mt-4 flex flex-wrap gap-2" aria-label="Reminder points">
            {sorted.map((value) => <li key={value}>
              <span className="flex h-9 items-center gap-1.5 rounded-lg bg-blue-50 pl-3 pr-1 text-sm font-medium text-blue-700 dark:bg-blue-950 dark:text-blue-300">
                {describeThreshold(value)}
                <button
                  type="button"
                  aria-label={`Remove ${describeThreshold(value)}`}
                  onClick={() => setDays(days.filter((day) => day !== value))}
                  className="grid size-7 place-items-center rounded-md hover:bg-blue-100 dark:hover:bg-blue-900"
                ><X size={14} /></button>
              </span>
            </li>)}
          </ul>}

      <div className="mt-5 border-t border-slate-200 pt-4 dark:border-slate-800">
        <p className="text-[13px] font-medium text-slate-500">Add a reminder</p>
        <div className="mt-2 flex flex-wrap gap-2">
          {presets.filter((option) => !days.includes(option.days)).map((option) => <Button
            key={option.days}
            variant="secondary"
            className="h-9 text-sm"
            disabled={full}
            onClick={() => add(option.days)}
          ><Plus size={15} />{option.label}</Button>)}
          {!days.includes(0) && <Button
            variant="secondary"
            className="h-9 text-sm"
            disabled={full}
            onClick={() => add(0)}
          ><Plus size={15} />On the day</Button>}
        </div>

        <form
          className="mt-3 flex items-end gap-2"
          onSubmit={(event) => {
            event.preventDefault()
            const value = Number(custom)
            if (Number.isInteger(value) && value >= 0 && value <= 365) {
              add(value)
              setCustom('')
            }
          }}
        >
          <label className="text-[13px] font-medium text-slate-500">
            Or a number of days
            <input
              className="input mt-1.5 h-9 w-32"
              value={custom}
              inputMode="numeric"
              onChange={(event) => setCustom(event.target.value)}
            />
          </label>
          <Button variant="secondary" className="h-9 text-sm" disabled={full || custom.trim() === ''}>Add</Button>
        </form>
        {full && <p className="mt-2 text-[13px] text-slate-500">
          {maximumThresholds} is the most that can be set — past that an owner stops reading them.
        </p>}
      </div>
    </section>

    <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="font-semibold">Who to tell</h2>
      <p className="mt-1 text-sm text-slate-500">
        Contract renewals go to these addresses — every contract, regardless of who is recorded as its
        owner. Leave the list empty and each notice goes to the contract's own owner instead. Warranty
        and licence notices always go to the person holding the asset.
      </p>

      {recipients.length === 0
        ? <p className="mt-4 rounded-lg border border-slate-200 bg-slate-50 p-3 text-sm text-slate-600 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-400">
            No addresses set, so each contract's owner is emailed.
          </p>
        : <ul className="mt-4 flex flex-wrap gap-2" aria-label="Reminder recipients">
            {recipients.map((address) => <li key={address}>
              <span className="flex h-9 items-center gap-1.5 rounded-lg bg-slate-100 pl-3 pr-1 text-sm font-medium text-slate-700 dark:bg-slate-800 dark:text-slate-200">
                {address}
                <button
                  type="button"
                  aria-label={`Remove ${address}`}
                  onClick={() => setRecipients(recipients.filter((entry) => entry !== address))}
                  className="grid size-7 place-items-center rounded-md hover:bg-slate-200 dark:hover:bg-slate-700"
                ><X size={14} /></button>
              </span>
            </li>)}
          </ul>}

      <form
        className="mt-4 flex items-end gap-2 border-t border-slate-200 pt-4 dark:border-slate-800"
        onSubmit={(event) => { event.preventDefault(); addRecipient() }}
      >
        <label className="flex-1 text-[13px] font-medium text-slate-500">
          Add an address
          <input
            className="input mt-1.5 h-9 w-full"
            type="email"
            placeholder="it-contracts@example.com"
            value={recipient}
            disabled={recipientsFull}
            onChange={(event) => setRecipient(event.target.value)}
          />
        </label>
        <Button
          variant="secondary"
          className="h-9 text-sm"
          disabled={recipientsFull || recipientInvalid || trimmedRecipient === ''}
        ><Plus size={15} />Add address</Button>
      </form>
      {recipientInvalid && <p className="mt-2 text-[13px] text-rose-600 dark:text-rose-400">
        That does not look like an email address.
      </p>}
      {recipientsFull && <p className="mt-2 text-[13px] text-slate-500">
        {maximumRecipients} is the most that can be set — use a distribution group for a wider audience.
      </p>}
    </section>

    <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <label className="flex items-center gap-3 text-sm">
        <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />
        {/* Off keeps the numbers, so switching back on does not mean setting them up again. */}
        <span>Send renewal reminders</span>
      </label>
      <p className="mt-1 text-sm text-slate-500">
        Turning this off stops the emails and keeps the points above, so it can be switched back on
        unchanged.
      </p>
    </section>

    <div className="flex items-center gap-3">
      <Button disabled={save.isPending || sorted.length === 0} onClick={() => save.mutate()}>
        {save.isPending ? 'Saving…' : 'Save'}
      </Button>
      {settings.data && settings.data.updatedBy !== 'default' && <p className="text-[13px] text-slate-500">
        Last changed by {settings.data.updatedBy}
      </p>}
    </div>
  </div>
}
