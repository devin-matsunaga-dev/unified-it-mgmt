import { useMutation, useQuery } from '@tanstack/react-query'
import { Camera, Check, ChevronLeft, PackagePlus, ScanLine, Trash2, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import {
  assetsApi, ciTypeLabel,
  type CiAttributeDefinition, type CiCustomField, type CiType, type IdentifyDeviceResponse,
} from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { FieldActionBar } from '../../layout/FieldShell'
import { cn } from '../../lib/utils'
import { confidenceLabel, confidenceTone, identifierKindLabel } from './identification'
import { useQrCamera } from './useQrCamera'

/**
 * Registering a device that has just arrived. It wears no label of ours — that gets printed once it
 * exists — so what is scanned here is the manufacturer's own, which is why the reader reads 1D as
 * well as QR: a Dell service tag is Code 39, most HP, Lenovo and Apple serials are Code 128.
 *
 * A device wears more than one barcode and no single one answers everything, so scans accumulate and
 * the whole set is re-identified after each. **Parsing and lookup are both server-side**: the browser
 * captures and displays, and never decides what a scanned string means.
 *
 * **Nothing is written until the technician presses Register.** An identification, however confident,
 * only fills the form in.
 */
const types: CiType[] = ['Hardware', 'Server', 'NetworkDevice']

export function FieldReceivePage() {
  const navigate = useNavigate()
  const [params] = useSearchParams()

  const [scans, setScans] = useState<string[]>(() => {
    const arrived = params.get('code')
    return arrived ? [arrived] : []
  })
  const [identified, setIdentified] = useState<IdentifyDeviceResponse | null>(null)
  const [typed, setTyped] = useState('')
  const [scanTarget, setScanTarget] = useState<'device' | string | null>(null)

  const [name, setName] = useState('')
  const [assetTag, setAssetTag] = useState('')
  const [serial, setSerial] = useState('')
  const [type, setType] = useState<CiType>('Hardware')
  const [attributes, setAttributes] = useState<Record<string, string>>({})
  const [customFields, setCustomFields] = useState<Record<string, string>>({})
  const [remember, setRemember] = useState(true)

  const schemas = useQuery({ queryKey: ['ci-type-schemas'], queryFn: assetsApi.listTypeSchemas })

  const identify = useMutation({
    mutationFn: (next: string[]) => assetsApi.identifyDevice(next),
    onSuccess: (response) => {
      setIdentified(response)
      response.rejected.forEach((value) => toast.error(`"${value}" is not a usable identifier.`))
      const { result } = response
      // Only blanks are filled. A technician who typed something meant it, and an identification that
      // overwrites their correction is worse than one that never ran.
      if (result.serialNumber && !serial.trim()) setSerial(result.serialNumber)
      setAttributes((current) => ({
        ...current,
        ...(result.manufacturer && !current.manufacturer?.trim() ? { manufacturer: result.manufacturer } : {}),
        ...(result.model && !current.model?.trim() ? { model: result.model } : {}),
      }))
      if (result.model && !name.trim()) setName(result.model)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  function addScan(value: string) {
    const trimmed = value.trim()
    if (!trimmed) return
    // The server collapses duplicates too; this keeps the on-screen list honest as they are taken.
    const next = scans.includes(trimmed) ? scans : [...scans, trimmed]
    setScans(next)
    setTyped('')
    identify.mutate(next)
  }

  function removeScan(scanned: string) {
    const next = scans.filter((scan) => scan !== scanned)
    setScans(next)
    if (next.length === 0) setIdentified(null)
    else identify.mutate(next)
  }

  const camera = useQrCamera((code) => {
    if (scanTarget && scanTarget !== 'device') {
      setAttributes((current) => ({ ...current, [scanTarget]: code }))
    } else {
      addScan(code)
    }
    setScanTarget(null)
    camera.stop()
  })

  // A code carried in from the scan screen has already failed a CI lookup, so it belongs to a device
  // nobody has registered — identify it on arrival rather than making the technician scan it twice.
  useEffect(() => {
    if (scans.length > 0) identify.mutate(scans)
    // Deliberately once, on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const create = useMutation({
    mutationFn: async () => {
      const ci = await assetsApi.createCi({
        type,
        name: name.trim(),
        assetTag: assetTag.trim() || null,
        serialNumber: serial.trim() || null,
        description: null,
        attributes,
        customFields,
        // Left to the server's InStock default: a CI is registered when it reaches the store room and
        // every later state is reached through a guarded transition.
      })
      // Written after the asset, deliberately. The device is what had to be recorded; a catalogue
      // mapping is a convenience, and one that fails must not cost the registration.
      const modelCode = identified?.identifiers.find((item) => item.kind === 'ModelIdentifier')?.value
      if (remember && modelCode && attributes.manufacturer?.trim() && attributes.model?.trim()) {
        try {
          await assetsApi.saveProductCatalogEntry({
            modelIdentifier: modelCode,
            manufacturer: attributes.manufacturer.trim(),
            model: attributes.model.trim(),
          })
        } catch {
          toast.error('The device was registered, but the model mapping was not saved.')
        }
      }
      return ci
    },
    onSuccess: (ci) => {
      toast.success(`${ci.name} is registered and in stock`)
      navigate(`/field/ci/${ci.id}`, { replace: true })
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const schema = (schemas.data ?? []).find((entry) => entry.type === type)
  const required: CiAttributeDefinition[] = schema?.attributes.filter((attribute) => attribute.isRequired) ?? []
  const fields: CiCustomField[] = schema?.customFields ?? []
  const requiredFields = fields.filter((field) => field.isRequired)
  const complete = name.trim() !== ''
    && required.every((definition) => (attributes[definition.key] ?? '').trim() !== '')
    && requiredFields.every((field) => (customFields[field.key] ?? '').trim() !== '')
  const live = camera.status === 'starting' || camera.status === 'scanning'
  const result = identified?.result

  return <>
    <Link to="/field/scan" className="inline-flex h-11 items-center gap-1 text-[15px] font-medium text-blue-600">
      <ChevronLeft size={18} />Scan
    </Link>
    <h1 className="mt-1 text-[22px] font-bold leading-tight">Receive a new asset</h1>
    <p className="mt-1 text-[15px] text-slate-500">
      Scan every barcode on the device — more scans, better answer.
    </p>

    <div className={live ? 'mt-4' : 'hidden'}>
      <div className="relative overflow-hidden rounded-xl border border-slate-200 bg-slate-900 dark:border-slate-800">
        <video ref={camera.videoRef} muted playsInline className="aspect-[4/3] w-full object-cover" />
        {/* Wide and short: a 1D barcode has to span the frame's width to decode, unlike a QR. */}
        <div className="pointer-events-none absolute inset-0 grid place-items-center">
          <div className="h-24 w-[85%] rounded-lg border-2 border-white/80" />
        </div>
        <button
          type="button"
          onClick={() => { setScanTarget(null); camera.stop() }}
          aria-label="Close camera"
          className="absolute right-2 top-2 grid size-11 place-items-center rounded-lg bg-black/50 text-white"
        ><X size={20} /></button>
      </div>
      <p className="mt-2 text-center text-[13px] text-slate-500">Hold the barcode across the frame.</p>
    </div>

    {camera.status === 'denied' && <p role="alert" className="mt-4 rounded-xl border border-amber-200 bg-amber-50 p-3 text-[15px] text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
      This browser is not allowed to use the camera. Allow it in Settings, or type the codes below.
    </p>}

    {!live && <>
      <section className="mt-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
        <h2 className="text-base font-semibold">Scanned</h2>
        {(identified?.identifiers.length ?? 0) === 0
          ? <p className="mt-2 text-[15px] text-slate-500">Nothing yet.</p>
          : <ul className="mt-3 space-y-2" aria-label="Scanned identifiers">
              {identified?.identifiers.map((identifier) => <li
                key={`${identifier.kind}:${identifier.value}`}
                className="flex items-center gap-3 rounded-lg border border-slate-200 p-2.5 dark:border-slate-700"
              >
                <Check size={17} className="shrink-0 text-green-600" />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[15px] font-medium tabular-nums">{identifier.value}</span>
                  <span className="block text-[13px] text-slate-500">{identifierKindLabel(identifier.kind)}</span>
                </span>
                <button
                  type="button"
                  aria-label={`Remove ${identifier.value}`}
                  onClick={() => removeScan(identifier.scanned)}
                  className="grid size-11 shrink-0 place-items-center rounded-lg text-slate-400"
                ><Trash2 size={17} /></button>
              </li>)}
            </ul>}

        <form
          className="mt-3 flex gap-2"
          onSubmit={(event) => { event.preventDefault(); addScan(typed) }}
        >
          <label htmlFor="field-receive-scan" className="sr-only">Type a code</label>
          <input
            id="field-receive-scan"
            value={typed}
            onChange={(event) => setTyped(event.target.value)}
            placeholder="Type a code"
            autoComplete="off"
            autoCapitalize="characters"
            // 16px, or iOS Safari zooms the page on focus.
            className="h-12 min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
          />
          <Button type="submit" variant="secondary" className="h-12 shrink-0" disabled={!typed.trim() || identify.isPending}>Add</Button>
        </form>

        <Button
          type="button"
          variant="secondary"
          className="mt-2 h-12 w-full text-[15px]"
          onClick={() => { setScanTarget('device'); void camera.start() }}
        ><Camera size={18} />{scans.length === 0 ? 'Scan a barcode' : 'Scan another'}</Button>
      </section>

      {result && <section className="mt-3 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
        <div className="flex items-center gap-2">
          <h2 className="text-base font-semibold">Detected</h2>
          <span className={cn('ml-auto shrink-0 rounded-md px-2 py-0.5 text-xs font-medium', confidenceTone(result.confidence))}>
            {confidenceLabel(result.confidence)}
          </span>
        </div>
        {result.confidence === 'Unknown'
          ? <p className="mt-2 text-[15px] text-slate-500">
              This device could not be identified from what has been scanned. Fill it in below — and
              leave "remember" ticked so the next one of these identifies itself.
            </p>
          : <>
              <p className="mt-2 text-[17px] font-semibold">{result.manufacturer} {result.model}</p>
              {result.deviceType && <p className="text-[15px] text-slate-500">{result.deviceType}</p>}
              <p className="mt-2 text-[13px] text-slate-500">Source: {result.source}</p>
              {result.confidence !== 'High' && <p className="mt-2 text-[13px] text-amber-700 dark:text-amber-400">
                Check this before registering — it is not an exact match.
              </p>}
            </>}
      </section>}

      <form
        className="mt-3"
        onSubmit={(event) => { event.preventDefault(); if (complete) create.mutate() }}
      >
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="text-base font-semibold">Register it</h2>

          <label htmlFor="field-receive-name" className="mt-3 block text-[13px] font-medium text-slate-500">What is it?</label>
          <input
            id="field-receive-name"
            value={name}
            onChange={(event) => setName(event.target.value)}
            maxLength={200}
            className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
          />

          <label htmlFor="field-receive-type" className="mt-4 block text-[13px] font-medium text-slate-500">Kind</label>
          <select
            id="field-receive-type"
            value={type}
            onChange={(event) => {
              setType(event.target.value as CiType)
              // Attributes belong to a type; carrying them across would submit fields the server
              // refuses for the new one.
              setAttributes({})
              setCustomFields({})
            }}
            className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
          >
            {(schemas.data?.map((entry) => entry.type).filter((value) => types.includes(value)) ?? types)
              .map((value) => <option key={value} value={value}>{ciTypeLabel(value)}</option>)}
          </select>

          {required.map((definition) => <div key={definition.key}>
            <label htmlFor={`field-receive-${definition.key}`} className="mt-4 block text-[13px] font-medium text-slate-500">
              {definition.label}
            </label>
            <div className="mt-1.5 flex gap-2">
              <input
                id={`field-receive-${definition.key}`}
                value={attributes[definition.key] ?? ''}
                onChange={(event) => setAttributes((current) => ({ ...current, [definition.key]: event.target.value }))}
                inputMode={definition.kind === 'Integer' ? 'numeric' : 'text'}
                autoComplete="off"
                className="h-12 min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
              />
              {definition.kind === 'Text' && <button
                type="button"
                aria-label={`Scan ${definition.label}`}
                onClick={() => { setScanTarget(definition.key); void camera.start() }}
                className="grid size-12 shrink-0 place-items-center rounded-lg border border-slate-200 bg-white text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300"
              ><ScanLine size={19} /></button>}
            </div>
          </div>)}

          <label htmlFor="field-receive-serial" className="mt-4 block text-[13px] font-medium text-slate-500">Serial number</label>
          <input
            id="field-receive-serial"
            value={serial}
            onChange={(event) => setSerial(event.target.value)}
            autoComplete="off"
            autoCapitalize="characters"
            className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
          />

          <label htmlFor="field-receive-tag" className="mt-4 block text-[13px] font-medium text-slate-500">Asset tag (optional)</label>
          <input
            id="field-receive-tag"
            value={assetTag}
            onChange={(event) => setAssetTag(event.target.value)}
            maxLength={64}
            autoComplete="off"
            autoCapitalize="characters"
            className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
          />

          <label className="mt-4 flex min-h-12 items-center gap-3 text-[15px]">
            <input
              type="checkbox"
              checked={remember}
              onChange={(event) => setRemember(event.target.checked)}
              className="size-5 rounded border-slate-300"
            />
            {/* Offered rather than done quietly: a mapping typed here is unverified and will describe
                every later device carrying the same product code. */}
            <span>Remember this model, so the next one identifies itself</span>
          </label>
        </section>

        <FieldActionBar>
          <Button type="submit" className="h-12 w-full text-[15px]" disabled={!complete || create.isPending}>
            <PackagePlus size={18} />{create.isPending ? 'Registering…' : 'Register it'}
          </Button>
        </FieldActionBar>
      </form>
    </>}
  </>
}
