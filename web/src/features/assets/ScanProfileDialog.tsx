import { useMutation } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { useState } from 'react'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { scanProfilesApi, type ScanProfile, type ScanProfileInput } from '../../api/monitoring'
import { Button } from '../../components/ui/Button'

/**
 * Create or edit one scan profile.
 *
 * Ranges and ports are typed as text and split here, rather than being a repeating row editor: the
 * accepted forms are `local`, a CIDR block, an inclusive span and a single address, and a list a
 * person can paste is worth more than four widgets. The API validates every entry and answers with
 * field errors, so the shapes are not re-implemented in the browser — `ScanRange` (C#) and
 * `ranges.py` already mirror each other by hand, and a third copy here would be a third thing to
 * drift.
 */
export function ScanProfileDialog({ profile, onClose, onSaved }: {
  profile: ScanProfile | null
  onClose: () => void
  onSaved: () => void | Promise<void>
}) {
  const [name, setName] = useState(profile?.name ?? '')
  const [description, setDescription] = useState(profile?.description ?? '')
  const [discoveryGroup, setDiscoveryGroup] = useState(profile?.discoveryGroup ?? 'default')
  const [ranges, setRanges] = useState((profile?.ranges ?? []).join(', '))
  const [ports, setPorts] = useState((profile?.ports ?? []).join(', '))
  const [intervalMinutes, setIntervalMinutes] = useState(String(profile?.intervalMinutes ?? 60))
  const [timeoutSeconds, setTimeoutSeconds] = useState(String(profile?.timeoutSeconds ?? 2))
  const [snmpEnabled, setSnmpEnabled] = useState(profile?.snmpEnabled ?? true)
  const [neighbourDiscoveryEnabled, setNeighbourDiscoveryEnabled] = useState(profile?.neighbourDiscoveryEnabled ?? true)
  const [isEnabled, setIsEnabled] = useState(profile?.isEnabled ?? true)
  const [scheduleEnabled, setScheduleEnabled] = useState(profile?.scheduleEnabled ?? true)
  const [errors, setErrors] = useState<Record<string, string>>({})

  const save = useMutation({
    mutationFn: (input: ScanProfileInput) =>
      profile ? scanProfilesApi.update(profile.id, input) : scanProfilesApi.create(input),
    onSuccess: async (saved) => {
      toast.success(profile ? `${saved.name} saved.` : `${saved.name} created.`)
      await onSaved()
    },
    onError: (error: ApiError) => {
      // Field errors land beside their input; anything else is a toast, following every other form.
      const fields = Object.entries(error.errors ?? {})
        .reduce<Record<string, string>>((all, [key, messages]) => {
          all[key.charAt(0).toLowerCase() + key.slice(1)] = messages[0]
          return all
        }, {})
      setErrors(fields)
      if (Object.keys(fields).length === 0) toast.error(error.message)
    },
  })

  function submit(event: React.FormEvent) {
    event.preventDefault()
    setErrors({})

    const parsedRanges = split(ranges)
    if (parsedRanges.length === 0) {
      setErrors({ ranges: 'A profile with no range has nothing to scan.' })
      return
    }

    save.mutate({
      name: name.trim(),
      description: description.trim() || null,
      discoveryGroup: discoveryGroup.trim() || null,
      ranges: parsedRanges,
      ports: split(ports).map(Number).filter((port) => Number.isInteger(port)),
      intervalMinutes: Number(intervalMinutes),
      timeoutSeconds: Number(timeoutSeconds),
      snmpEnabled,
      neighbourDiscoveryEnabled,
      isEnabled,
      scheduleEnabled,
    })
  }

  return <div className="fixed inset-0 z-20 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true"
    aria-label={profile ? `Edit ${profile.name}` : 'New scan profile'}>
    <form onSubmit={submit}
      className="max-h-full w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-lg font-semibold">{profile ? `Edit ${profile.name}` : 'New scan profile'}</h2>
      <p className="mt-1 text-sm text-slate-500">
        A range to sweep, how often, and how hard to interrogate whatever answers.
      </p>

      <div className="mt-5 grid gap-4 sm:grid-cols-2">
        <Field label="Name" htmlFor="profile-name" required error={errors.name}>
          <input id="profile-name" className="input h-11" value={name} maxLength={200}
            onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Discovery group" htmlFor="profile-group" error={errors.discoveryGroup}
          hint="Which scanner runs it. Leave as default unless you run more than one.">
          <input id="profile-group" className="input h-11" value={discoveryGroup} maxLength={100}
            onChange={(event) => setDiscoveryGroup(event.target.value)} />
        </Field>
      </div>

      <div className="mt-4">
        <Field label="Description" htmlFor="profile-description" error={errors.description}>
          <input id="profile-description" className="input h-11" value={description} maxLength={2000}
            onChange={(event) => setDescription(event.target.value)} />
        </Field>
      </div>

      <div className="mt-4">
        <Field label="Ranges" htmlFor="profile-ranges" required error={errors.ranges}
          hint="Comma separated. A CIDR block (10.0.0.0/24), a span (10.0.0.5-40), a single address, or local — the subnet the scanner itself sits on.">
          <input id="profile-ranges" className="input h-11 font-mono" value={ranges}
            onChange={(event) => setRanges(event.target.value)} />
        </Field>
      </div>

      <div className="mt-4">
        <Field label="Ports" htmlFor="profile-ports" error={errors.ports}
          hint="Comma separated, and optional. With none, the sweep is ping only — the cheapest useful scan there is. Every port is tried against every address, so the cost is addresses × ports.">
          <input id="profile-ports" className="input h-11 font-mono" value={ports}
            onChange={(event) => setPorts(event.target.value)} />
        </Field>
      </div>

      <div className="mt-4 grid gap-4 sm:grid-cols-2">
        <Field label="Interval (minutes)" htmlFor="profile-interval" error={errors.intervalMinutes}
          hint="How often it runs when scheduled scanning is on.">
          <input id="profile-interval" type="number" min={1} className="input h-11" value={intervalMinutes}
            onChange={(event) => setIntervalMinutes(event.target.value)} />
        </Field>
        <Field label="Timeout (seconds)" htmlFor="profile-timeout" error={errors.timeoutSeconds}
          hint="Per probe against one address, not for the whole sweep.">
          <input id="profile-timeout" type="number" min={1} className="input h-11" value={timeoutSeconds}
            onChange={(event) => setTimeoutSeconds(event.target.value)} />
        </Field>
      </div>

      <fieldset className="mt-5 rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <legend className="px-1 text-[13px] font-medium text-slate-600 dark:text-slate-300">How thoroughly</legend>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={snmpEnabled} onChange={(event) => setSnmpEnabled(event.target.checked)} />
          Ask what answers to identify itself over SNMP
        </label>
        <label className="mt-2 flex items-center gap-2 text-sm">
          <input type="checkbox" checked={neighbourDiscoveryEnabled} disabled={!snmpEnabled}
            onChange={(event) => setNeighbourDiscoveryEnabled(event.target.checked)} />
          Walk LLDP and CDP for neighbours
        </label>
        <p className="mt-2 text-xs text-slate-500">
          The neighbour tables are the expensive part — two walks per device — and an estate of servers
          has nothing to report in them.
        </p>
      </fieldset>

      <fieldset className="mt-4 rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <legend className="px-1 text-[13px] font-medium text-slate-600 dark:text-slate-300">When it runs</legend>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={isEnabled} onChange={(event) => setIsEnabled(event.target.checked)} />
          Enabled
        </label>
        <label className="mt-2 flex items-center gap-2 text-sm">
          <input type="checkbox" checked={scheduleEnabled} disabled={!isEnabled}
            onChange={(event) => setScheduleEnabled(event.target.checked)} />
          Run on its interval
        </label>
        <p className="mt-2 text-xs text-slate-500">
          Two different instructions. Disabled removes the profile from every scanner, so it cannot be
          scanned at all; clearing only the interval leaves it there to be scanned on demand.
        </p>
      </fieldset>

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save'}</Button>
      </div>
    </form>
  </div>
}

/**
 * The hint sits outside the label deliberately: anything inside one becomes part of the field's
 * accessible name, which is the defect WP-5.7 hit and WP-5.9 met again.
 */
function Field({ label, htmlFor, required, error, hint, children }: {
  label: string
  htmlFor: string
  required?: boolean
  error?: string
  hint?: string
  children: ReactNode
}) {
  return <div>
    <label htmlFor={htmlFor} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">
      {label}{required && <span aria-hidden="true" className="ml-1 text-red-600">*</span>}
      {required && <span className="sr-only"> (required)</span>}
    </label>
    {children}
    {error
      ? <span role="alert" className="mt-1.5 block text-xs text-red-600">{error}</span>
      : hint && <span className="mt-1.5 block text-xs text-slate-500">{hint}</span>}
  </div>
}

function split(value: string) {
  return value.split(',').map((entry) => entry.trim()).filter(Boolean)
}
