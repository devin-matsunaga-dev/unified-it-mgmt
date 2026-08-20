import { useMutation, useQuery } from '@tanstack/react-query'
import { Camera, Check, ChevronLeft, PackagePlus, ScanLine, Trash2, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import {
  assetsApi, ciTypeLabel,
  type Ci, type CiAttributeDefinition, type CiCustomField, type CiType, type IdentifyDeviceResponse,
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
  /**
   * What the next read is for. 'device' adds it to the identification set; 'serial' declares it to be
   * the serial number; anything else names an attribute field.
   */
  const [scanTarget, setScanTarget] = useState<'device' | 'serial' | string | null>(null)

  const [name, setName] = useState('')
  const [assetTag, setAssetTag] = useState('')
  const [serial, setSerial] = useState('')
  const [type, setType] = useState<CiType>('Hardware')
  const [attributes, setAttributes] = useState<Record<string, string>>({})
  const [customFields, setCustomFields] = useState<Record<string, string>>({})
  const [remember, setRemember] = useState(true)
  /**
   * Which scanned code the catalogue entry is keyed on. Defaults to whatever the server classified as
   * a product identifier; settable by tapping a row, because a bare product code comes back
   * unclassified and the catalogue would otherwise be keyed on nothing at all.
   */
  const [modelCode, setModelCode] = useState<string | null>(null)
  /**
   * Mirrors the two fields above, read-only-for-timing. A scan sets state and posts the identify call
   * in the same breath, so the response's handler closes over the render *before* the assignment —
   * and a rule that reads stale state cannot see that a value was already spoken for. That is exactly
   * how the model code ended up in the serial field.
   */
  const modelCodeRef = useRef<string | null>(null)
  const serialRef = useRef('')
  /** True when the read was started from the Model code field rather than the section above. */
  const replaceModelRef = useRef(false)

  function assignModelCode(value: string | null) {
    modelCodeRef.current = value
    setModelCode(value)
  }

  function assignSerial(value: string) {
    serialRef.current = value
    setSerial(value)
  }
  /** An asset already registered under one of these codes. The device is not new. */
  const [existing, setExisting] = useState<Ci | null>(null)

  const schemas = useQuery({ queryKey: ['ci-type-schemas'], queryFn: assetsApi.listTypeSchemas })

  const identify = useMutation({
    mutationFn: (next: string[]) => assetsApi.identifyDevice(next),
    onSuccess: (response) => {
      setIdentified(response)
      response.rejected.forEach((value) => toast.error(`"${value}" is not a usable identifier.`))
      const { result } = response
      // Only blanks are filled. A technician who typed something meant it, and an identification that
      // overwrites their correction is worse than one that never ran.
      if (!serialRef.current.trim()) {
        // The server's answer first. Failing that, an unclassified scan — because a bare alphanumeric
        // is what most manufacturers print, and refusing to *classify* it is no reason to make
        // somebody retype what they just scanned. **A code already claimed as the model is never a
        // candidate**: the primary scan is the model code, so without this the thing a technician
        // just aimed at as a product code lands in the serial field. Only when exactly one remains —
        // with two, which is the serial is a guess, and the row can be tapped instead.
        const unclassified = response.identifiers.filter((item) =>
          item.kind === 'Unknown' && item.value !== modelCodeRef.current)
        const candidate = result.serialNumber
          ?? (unclassified.length === 1 ? unclassified[0].value : null)
        if (candidate) assignSerial(candidate)
      }
      setAttributes((current) => ({
        ...current,
        ...(result.manufacturer && !current.manufacturer?.trim() ? { manufacturer: result.manufacturer } : {}),
        ...(result.model && !current.model?.trim() ? { model: result.model } : {}),
      }))
      if (result.model && !name.trim()) setName(result.model)
      if (!modelCodeRef.current) {
        const product = response.identifiers.find((item) => item.kind === 'ModelIdentifier')
        if (product) assignModelCode(product.value)
      }
      // Restored after being lost in the rewrite onto this API: a device can already be in the CMDB
      // from a purchase-order import or a previous receipt, and letting somebody fill in a whole form
      // before the server refuses the duplicate is the worst possible moment to tell them.
      void findExisting(response)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  /**
   * Whether any scanned code already belongs to a registered asset. Each candidate is looked up in
   * turn; a 404 is the ordinary answer and never surfaces as an error.
   */
  async function findExisting(response: IdentifyDeviceResponse) {
    for (const identifier of response.identifiers) {
      if (identifier.kind === 'ModelIdentifier') continue
      try {
        const found = await assetsApi.lookupCi(identifier.value)
        setExisting(found)
        return
      } catch {
        // Not registered under this code, which is the expected case for a new device.
      }
    }
    setExisting(null)
  }

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
    if (scanTarget === 'model') {
      // The primary scan. A product code is what makes the next device of this model identify
      // itself; a serial names one unit and can never do that, which is why the serial has been
      // demoted to its own field and this is what the main button aims at.
      //
      // A read aimed at the Model code field replaces whatever is there — the technician is
      // correcting it. The section button above only claims an empty one, so scanning extra codes
      // for a better vendor answer cannot displace the key the catalogue will use.
      if (!modelCodeRef.current || replaceModelRef.current) assignModelCode(code)
      addScan(code)
    } else if (scanTarget === 'serial') {
      setSerial(code)
      // Sent to the identifier as a labelled serial rather than a bare string. The technician aimed
      // at the serial barcode and said so, which is a better source of truth than any pattern the
      // parser could apply — and it means the identification uses it as a serial too, rather than
      // carrying it as one more unclassified code.
      addScan(`S/N: ${code}`)
    } else if (scanTarget && scanTarget !== 'device') {
      setAttributes((current) => ({ ...current, [scanTarget]: code }))
    } else {
      addScan(code)
    }
    replaceModelRef.current = false
    setScanTarget(null)
    camera.stop()
  }, {
    // A wide, short window matching the guide drawn over the viewfinder: device labels crowd the
    // serial, the product code and a shipping reference together, and without this the decoder
    // returns whichever it resolved first rather than the one being aimed at.
    region: { widthRatio: 0.85, heightRatio: 0.28 },
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
      Start with the model or product code — <span className="font-medium">P/N</span>,{' '}
      <span className="font-medium">PID</span>, <span className="font-medium">MTM</span> or{' '}
      <span className="font-medium">SKU</span>. That is what lets the next one of these identify itself.
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
      <p className="mt-2 text-center text-[13px] text-slate-500">
        {scanTarget === 'serial'
          ? 'Reading the serial number. Only the barcode inside the frame counts.'
          : scanTarget === 'model'
            ? 'Reading the model or product code. Only the barcode inside the frame counts.'
            : 'Hold one barcode inside the frame — nothing outside it is read.'}
      </p>
    </div>

    {camera.status === 'denied' && <p role="alert" className="mt-4 rounded-xl border border-amber-200 bg-amber-50 p-3 text-[15px] text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
      This browser is not allowed to use the camera. Allow it in Settings, or type the codes below.
    </p>}

    {!live && <>
      <section className="mt-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
        <h2 className="text-base font-semibold">Model or product code</h2>
        <p className="mt-1 text-[13px] text-slate-500">
          The serial has its own field below — a serial names one device and cannot identify the next.
        </p>
        {(identified?.identifiers.length ?? 0) === 0
          ? <p className="mt-3 text-[15px] text-slate-500">No code scanned yet.</p>
          : <ul className="mt-3 space-y-2" aria-label="Scanned identifiers">
              {identified?.identifiers.map((identifier) => <li
                key={`${identifier.kind}:${identifier.value}`}
                className="flex items-center gap-3 rounded-lg border border-slate-200 p-2.5 dark:border-slate-700"
              >
                <Check size={17} className="shrink-0 text-green-600" />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[15px] font-medium tabular-nums">{identifier.value}</span>
                  <span className="block text-[13px] text-slate-500">
                    {identifier.value === serial ? 'Serial number'
                      : identifier.value === modelCode ? 'Model code'
                      : identifierKindLabel(identifier.kind)}
                  </span>
                </span>
                {/* Tap to place it: whichever barcode a technician aimed at, they know what it is,
                    and that beats any pattern the parser could apply to a bare string. */}
                {/* Only the model code is placeable from a row. The serial has a dedicated field with
                    its own scan button, so a second way to set it was two controls for one job. */}
                <button
                  type="button"
                  onClick={() => assignModelCode(identifier.value)}
                  disabled={modelCode === identifier.value}
                  className="h-11 shrink-0 rounded-lg px-2 text-[13px] font-medium text-blue-600 disabled:text-slate-400"
                >Model</button>
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
          className="mt-2 h-12 w-full text-[15px]"
          onClick={() => { replaceModelRef.current = false; setScanTarget('model'); void camera.start() }}
        ><Camera size={18} />{modelCode ? 'Scan another code' : 'Scan the model code'}</Button>
      </section>

      {existing && <section role="alert" className="mt-3 rounded-xl border border-amber-200 bg-amber-50 p-5 dark:border-amber-500/30 dark:bg-amber-500/10">
        <h2 className="text-base font-semibold text-amber-900 dark:text-amber-300">Already registered</h2>
        <p className="mt-1 text-[15px] text-amber-800 dark:text-amber-300">
          <span className="font-semibold">{existing.name}</span> already carries one of these codes.
          Registering it again would make a second record for one device.
        </p>
        <button
          type="button"
          onClick={() => navigate(`/field/ci/${existing.id}`)}
          className="mt-3 flex h-12 w-full items-center justify-center rounded-lg bg-amber-600 text-[15px] font-medium text-white"
        >Open it instead</button>
      </section>}

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

          <label htmlFor="field-receive-name" className="mt-3 block text-[13px] font-medium text-slate-500">Device name</label>
          <p className="mt-0.5 text-[13px] text-slate-500">What this one is called — "Comms room switch".</p>
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

          <label htmlFor="field-receive-model-code" className="mt-4 block text-[13px] font-medium text-slate-500">
            Model code
          </label>
          <div className="mt-1.5 flex gap-2">
            <input
              id="field-receive-model-code"
              value={modelCode ?? ''}
              onChange={(event) => assignModelCode(event.target.value.trim() ? event.target.value : null)}
              placeholder="P/N, PID, MTM or SKU"
              autoComplete="off"
              autoCapitalize="characters"
              className="h-12 min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
            />
            <button
              type="button"
              aria-label="Scan model code"
              onClick={() => { replaceModelRef.current = true; setScanTarget('model'); void camera.start() }}
              className="grid size-12 shrink-0 place-items-center rounded-lg border border-slate-200 bg-white text-slate-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300"
            ><ScanLine size={19} /></button>
          </div>
          {/* Said here rather than only beside the checkbox: this is the field it is about. */}
          <p className="mt-1.5 text-[13px] text-slate-500">
            What every device of this model carries. It is the key the next one is recognised by.
          </p>

          <label htmlFor="field-receive-serial" className="mt-4 block text-[13px] font-medium text-slate-500">Serial number</label>
          <div className="mt-1.5 flex gap-2">
            <input
              id="field-receive-serial"
              value={serial}
              onChange={(event) => assignSerial(event.target.value)}
              autoComplete="off"
              autoCapitalize="characters"
              className="h-12 min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
            />
            <button
              type="button"
              aria-label="Scan serial number"
              onClick={() => { setScanTarget('serial'); void camera.start() }}
              className="grid size-12 shrink-0 place-items-center rounded-lg border border-slate-200 bg-white text-slate-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300"
            ><ScanLine size={19} /></button>
          </div>

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
          {/* Never silently no-op. Without a product code there is no key to remember the model
              against, and a ticked box that saves nothing is worse than an unticked one. */}
          {remember && !modelCode && <p className="mt-2 rounded-lg border border-amber-200 bg-amber-50 p-3 text-[13px] text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
            No model code yet, so there is nothing to remember it by. Scan the device's
            <strong> P/N</strong>, <strong>PID</strong>, <strong>MTM</strong> or <strong>SKU</strong>{' '}
            barcode above — or tap <strong>Model</strong> on one already scanned.
          </p>}
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
