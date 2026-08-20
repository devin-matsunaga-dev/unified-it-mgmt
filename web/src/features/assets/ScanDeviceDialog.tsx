import { useMutation } from '@tanstack/react-query'
import { Check, ScanLine, Trash2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'
import { assetsApi, type IdentifyDeviceResponse } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { confidenceLabel, confidenceTone, identifierKindLabel } from '../field/identification'
import type { CiFormSeed } from './CiFormDialog'

/**
 * Identifying a device at a desk, before the New CI form opens. The scanner here is a handheld wedge
 * rather than a camera: it types its code and presses Enter, so the field holds focus and every
 * submit is one scan. The phone's own flow lives in the field surface and shares this API, this
 * wording and this rule — nothing is created until a person confirms.
 */
export function ScanDeviceDialog({ open, onClose, onConfirm }: {
  open: boolean
  onClose: () => void
  onConfirm: (seed: CiFormSeed) => void
}) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [scans, setScans] = useState<string[]>([])
  const [typed, setTyped] = useState('')
  const [identified, setIdentified] = useState<IdentifyDeviceResponse | null>(null)

  const identify = useMutation({
    mutationFn: (next: string[]) => assetsApi.identifyDevice(next),
    onSuccess: (response) => {
      setIdentified(response)
      response.rejected.forEach((value) => toast.error(`"${value}" is not a usable identifier.`))
    },
    onError: (error: Error) => toast.error(error.message),
  })

  // Reopening starts a new device. Carrying the previous one's scans over would identify a machine
  // that is no longer on the desk.
  useEffect(() => {
    if (!open) return
    setScans([])
    setTyped('')
    setIdentified(null)
    // A wedge scanner fires whenever its trigger is pulled, so the field has to be holding focus
    // before anyone thinks to click it.
    inputRef.current?.focus()
  }, [open])

  if (!open) return null

  const result = identified?.result

  function add(value: string) {
    const trimmed = value.trim()
    if (!trimmed) return
    const next = scans.includes(trimmed) ? scans : [...scans, trimmed]
    setScans(next)
    setTyped('')
    identify.mutate(next)
    inputRef.current?.focus()
  }

  function remove(scanned: string) {
    const next = scans.filter((scan) => scan !== scanned)
    setScans(next)
    if (next.length === 0) setIdentified(null)
    else identify.mutate(next)
  }

  return <div className="fixed inset-0 z-20 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label="Scan device">
    <div className="max-h-[90vh] w-full max-w-[560px] overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-lg font-semibold">Scan device</h2>
      <p className="mt-1 text-sm text-slate-500">
        Scan every barcode on the device — the serial and the product or model code. More scans, better answer.
      </p>

      <form className="mt-4 flex gap-2" onSubmit={(event) => { event.preventDefault(); add(typed) }}>
        <label htmlFor="scan-device-code" className="sr-only">Scan or type a code</label>
        <input
          id="scan-device-code"
          ref={inputRef}
          value={typed}
          onChange={(event) => setTyped(event.target.value)}
          placeholder="Scan or type a code"
          autoComplete="off"
          className="input h-11 flex-1"
        />
        <Button type="submit" variant="secondary" className="h-11" disabled={!typed.trim() || identify.isPending}>Add</Button>
      </form>

      {(identified?.identifiers.length ?? 0) > 0 && <ul className="mt-4 space-y-2" aria-label="Scanned identifiers">
        {identified?.identifiers.map((identifier) => <li
          key={`${identifier.kind}:${identifier.value}`}
          className="flex items-center gap-3 rounded-lg border border-slate-200 p-2.5 dark:border-slate-700"
        >
          <Check size={16} className="shrink-0 text-green-600" />
          <span className="min-w-0 flex-1">
            <span className="block truncate text-sm font-medium tabular-nums">{identifier.value}</span>
            <span className="block text-[13px] text-slate-500">{identifierKindLabel(identifier.kind)}</span>
          </span>
          <button
            type="button"
            aria-label={`Remove ${identifier.value}`}
            onClick={() => remove(identifier.scanned)}
            className="grid size-9 shrink-0 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800"
          ><Trash2 size={16} /></button>
        </li>)}
      </ul>}

      {result && <section className="mt-4 rounded-xl border border-slate-200 p-4 dark:border-slate-700">
        <div className="flex items-center gap-2">
          <h3 className="text-sm font-semibold">Detected device</h3>
          <span className={cn('ml-auto rounded-md px-2 py-0.5 text-xs font-medium', confidenceTone(result.confidence))}>
            {confidenceLabel(result.confidence)}
          </span>
        </div>
        {result.confidence === 'Unknown'
          ? <p className="mt-2 text-sm text-slate-500">
              This device could not be identified from what has been scanned. Confirm anyway to carry
              the serial into the form and fill the rest in by hand.
            </p>
          : <>
              <p className="mt-2 text-base font-semibold">{result.manufacturer} {result.model}</p>
              {result.deviceType && <p className="text-sm text-slate-500">{result.deviceType}</p>}
              <p className="mt-2 text-[13px] text-slate-500">Identification source: {result.source}</p>
              {result.confidence !== 'High' && <p className="mt-2 text-[13px] text-amber-700 dark:text-amber-400">
                Not an exact match — check it against the device before saving.
              </p>}
            </>}
      </section>}

      <div className="mt-6 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button
          disabled={(identified?.identifiers.length ?? 0) === 0}
          onClick={() => onConfirm(toSeed(identified))}
        ><ScanLine size={17} />Confirm</Button>
      </div>
    </div>
  </div>
}

/**
 * What the identification carries into the New CI form. Manufacturer and model go in as attributes
 * because that is where a Hardware CI keeps them; the serial belongs to this device and nothing else.
 * The form is still empty of everything the technician has to decide, and still saves nothing on its
 * own — confirming an identification opens a form, it does not create an asset.
 */
export function toSeed(identified: IdentifyDeviceResponse | null): CiFormSeed {
  const result = identified?.result
  if (!result) return {}

  const attributes: Record<string, string> = {}
  if (result.manufacturer) attributes.manufacturer = result.manufacturer
  if (result.model) attributes.model = result.model

  return {
    name: result.model ?? undefined,
    serialNumber: result.serialNumber
      ?? identified?.identifiers.find((item) => item.kind === 'SerialNumber')?.value
      ?? undefined,
    attributes: Object.keys(attributes).length > 0 ? attributes : undefined,
  }
}
