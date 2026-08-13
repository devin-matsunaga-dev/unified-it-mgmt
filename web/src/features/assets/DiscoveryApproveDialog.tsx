import { useMemo, useState } from 'react'
import { ApiError } from '../../api/client'
import {
  assetsApi,
  ciTypeLabel,
  ciTypes,
  discoveryApi,
  type ApproveDiscoveredDeviceInput,
  type CiAttributeDefinition,
  type CiType,
  type DiscoveredDevice,
} from '../../api/assets'
import { useQuery } from '@tanstack/react-query'
import { Button } from '../../components/ui/Button'
import { schemaFor, validateAttributes } from './ciFields'

/**
 * Turning a discovery into a CI. A confirmation with edits rather than a form typed from nothing: the
 * scan has already filled in what it observed, and the fields it left blank are the ones it genuinely
 * cannot know.
 *
 * Two modes, because approving means two different things. Ordinarily it creates a CI. When the card
 * came back ambiguous — two CIs claimed the device — it instead attaches the discovery to whichever
 * one the reviewer picks, and creates nothing.
 */
export function DiscoveryApproveDialog({ device, onClose, onApproved }: {
  device: DiscoveredDevice
  onClose: () => void
  onApproved: (device: DiscoveredDevice) => void
}) {
  const schemas = useQuery({ queryKey: ['ci-type-schemas'], queryFn: assetsApi.listTypeSchemas })
  const canAttach = device.contenders.length > 0

  const [mode, setMode] = useState<'create' | 'attach'>(canAttach ? 'attach' : 'create')
  const [type, setType] = useState<CiType>(device.suggestedType)
  const [name, setName] = useState(device.suggestedName)
  const [assetTag, setAssetTag] = useState('')
  const [serialNumber, setSerialNumber] = useState('')
  const [attachTo, setAttachTo] = useState(device.contenders[0]?.ciId ?? '')
  const [enrollMonitoring, setEnrollMonitoring] = useState(false)
  const [pollerGroup, setPollerGroup] = useState('')
  const [note, setNote] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)

  const definitions: CiAttributeDefinition[] = useMemo(
    () => schemaFor(schemas.data ?? [], type)?.attributes ?? [],
    [schemas.data, type],
  )

  // Seeded from the scan and re-seeded whenever the type changes, because which attributes discovery
  // can fill depends on the type: a management IP for a network device, a hostname for a server.
  const [attributes, setAttributes] = useState<Record<string, string>>(device.suggestedAttributes)
  function changeType(next: CiType) {
    setType(next)
    setAttributes(next === device.suggestedType ? device.suggestedAttributes : {})
    setErrors({})
  }

  async function submit() {
    setError(null)
    if (mode === 'attach') {
      if (!attachTo) {
        setErrors({ ciId: 'Choose the CI this device already is.' })
        return
      }
    } else {
      const attributeErrors = validateAttributes(definitions, attributes)
      if (Object.keys(attributeErrors).length > 0 || !name.trim()) {
        setErrors({
          ...Object.fromEntries(Object.entries(attributeErrors).map(([key, message]) => [`attributes.${key}`, message])),
          ...(name.trim() ? {} : { name: 'Name is required.' }),
        })
        return
      }
    }

    const input: ApproveDiscoveredDeviceInput = mode === 'attach'
      ? { ciId: attachTo, enrollMonitoring, pollerGroup: pollerGroup.trim() || null, note: note.trim() || null }
      : {
          type,
          name: name.trim(),
          assetTag: assetTag.trim() || null,
          serialNumber: serialNumber.trim() || null,
          attributes: Object.fromEntries(
            definitions.map((definition) => [definition.key, (attributes[definition.key] ?? '').trim()])
              .filter(([, value]) => value !== ''),
          ),
          enrollMonitoring,
          pollerGroup: pollerGroup.trim() || null,
          note: note.trim() || null,
        }

    setPending(true)
    try {
      onApproved(await discoveryApi.approveDiscovered(device.id, input))
    } catch (caught) {
      // Server field errors land beside their inputs; anything else is a sentence at the top. A 409
      // means somebody else settled this card while it was open, which is worth saying plainly.
      if (caught instanceof ApiError && caught.errors) {
        setErrors(Object.fromEntries(Object.entries(caught.errors).map(([key, messages]) => [key, messages.join(' ')])))
      } else {
        setError(caught instanceof Error ? caught.message : 'The approval failed.')
      }
    } finally {
      setPending(false)
    }
  }

  return <div className="fixed inset-0 z-20 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label="Approve discovered device">
    <div className="max-h-full w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-lg font-semibold">Approve {device.suggestedName}</h2>
      <p className="mt-1 text-sm text-slate-500">
        Found at {device.address} by {device.scanProfileName}. Discovery has filled in what it saw; the rest is yours.
      </p>
      {error && <p role="alert" className="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700 dark:border-red-900 dark:bg-red-950">{error}</p>}

      {canAttach && <fieldset className="mt-5 rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <legend className="px-1 text-[13px] font-medium text-slate-600 dark:text-slate-300">This device is</legend>
        <label className="flex items-center gap-2 text-sm">
          <input type="radio" name="approve-mode" checked={mode === 'attach'} onChange={() => setMode('attach')} />
          A CI that already exists
        </label>
        {mode === 'attach' && <Field label="Which one" htmlFor="approve-attach" required error={errors.ciId}>
          <select id="approve-attach" className="input h-11" value={attachTo} onChange={(event) => setAttachTo(event.target.value)}>
            {device.contenders.map((contender) => <option key={contender.ciId} value={contender.ciId}>
              {contender.name} ({ciTypeLabel(contender.type)})
            </option>)}
          </select>
        </Field>}
        <label className="mt-3 flex items-center gap-2 text-sm">
          <input type="radio" name="approve-mode" checked={mode === 'create'} onChange={() => setMode('create')} />
          Something new
        </label>
      </fieldset>}

      {mode === 'create' && <>
        <div className="mt-5 grid gap-4 sm:grid-cols-2">
          <Field label="Type" htmlFor="approve-type">
            <select id="approve-type" className="input h-11" value={type} onChange={(event) => changeType(event.target.value as CiType)}>
              {ciTypes.map((option) => <option key={option} value={option}>{ciTypeLabel(option)}</option>)}
            </select>
          </Field>
          <Field label="Name" htmlFor="approve-name" required error={errors.name}>
            <input id="approve-name" className="input h-11" value={name} onChange={(event) => setName(event.target.value)} aria-invalid={Boolean(errors.name)} />
          </Field>
          <Field label="Asset tag" htmlFor="approve-asset-tag" error={errors.assetTag}>
            <input id="approve-asset-tag" className="input h-11" value={assetTag} onChange={(event) => setAssetTag(event.target.value)} />
          </Field>
          <Field label="Serial number" htmlFor="approve-serial" error={errors.serialNumber}>
            <input id="approve-serial" className="input h-11" value={serialNumber} onChange={(event) => setSerialNumber(event.target.value)} />
          </Field>
        </div>

        <h3 className="mt-6 text-[13px] font-medium text-slate-500">{ciTypeLabel(type)} attributes</h3>
        <div className="mt-2 grid gap-4 sm:grid-cols-2">
          {definitions.map((definition) => {
            const id = `approve-attribute-${definition.key}`
            const discovered = device.suggestedAttributes[definition.key] !== undefined
              && device.suggestedAttributes[definition.key] === attributes[definition.key]
            return <Field key={definition.key} label={definition.label} htmlFor={id} required={definition.isRequired}
              error={errors[`attributes.${definition.key}`]} hint={discovered ? 'From the scan' : undefined}>
              <input id={id} className="input h-11"
                type={definition.kind === 'Integer' ? 'number' : 'text'}
                inputMode={definition.kind === 'Integer' ? 'numeric' : undefined}
                min={definition.kind === 'Integer' ? 0 : undefined}
                value={attributes[definition.key] ?? ''}
                aria-invalid={Boolean(errors[`attributes.${definition.key}`])}
                onChange={(event) => setAttributes((current) => ({ ...current, [definition.key]: event.target.value }))} />
            </Field>
          })}
        </div>
      </>}

      <fieldset className="mt-6 rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <legend className="px-1 text-[13px] font-medium text-slate-600 dark:text-slate-300">Monitoring</legend>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={enrollMonitoring} onChange={(event) => setEnrollMonitoring(event.target.checked)} />
          Also monitor it — creates a device at {device.address} with a reachability check
        </label>
        {enrollMonitoring && <div className="mt-3">
          <Field label="Poller group" htmlFor="approve-poller-group" hint="Leave blank for the platform default.">
            <input id="approve-poller-group" className="input h-11" value={pollerGroup} onChange={(event) => setPollerGroup(event.target.value)} />
          </Field>
        </div>}
      </fieldset>

      <div className="mt-4">
        <Field label="Note" htmlFor="approve-note" hint="Why this is an asset. Kept on the record.">
          <textarea id="approve-note" rows={2} className="input py-2" value={note} onChange={(event) => setNote(event.target.value)} />
        </Field>
      </div>

      <div className="mt-6 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose} disabled={pending}>Cancel</Button>
        <Button onClick={() => void submit()} disabled={pending}>{pending ? 'Approving…' : 'Approve'}</Button>
      </div>
    </div>
  </div>
}

function Field({ label, htmlFor, required, error, hint, children }: {
  label: string
  htmlFor: string
  required?: boolean
  error?: string
  hint?: string
  children: React.ReactNode
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
