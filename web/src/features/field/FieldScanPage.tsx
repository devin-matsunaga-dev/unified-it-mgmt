import { useMutation } from '@tanstack/react-query'
import { Camera, ScanLine, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { assetsApi } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { FieldActionBar } from '../../layout/FieldShell'
import { useQrCamera } from './useQrCamera'

/**
 * Where "Scan next" goes. The camera reads the QR in-app so a technician working through a stockroom
 * never leaves the page; the typed field stays for a sticker too scuffed to read, a handheld wedge
 * scanner, and the tag someone already knows by heart.
 *
 * A decoded QR is the label's full URL, which the server's lookup already understands (CiLabelCodes
 * .TryReadCiId), so it goes through the same endpoint as a typed tag and the phone parses nothing.
 */
export function FieldScanPage() {
  const navigate = useNavigate()
  const inputRef = useRef<HTMLInputElement>(null)
  const [code, setCode] = useState('')

  // Which input produced the lookup in flight. A camera read that resolves to nothing should put the
  // technician back at the viewfinder — they are still holding the phone at a label. A mistyped tag
  // should not open the camera at all.
  const fromCamera = useRef(false)

  const lookup = useMutation({
    mutationFn: (scanned: string) => assetsApi.lookupCi(scanned),
    onSuccess: (ci) => {
      setCode('')
      camera.stop()
      navigate(`/field/ci/${ci.id}`)
    },
    onError: () => {
      if (fromCamera.current) void camera.start()
    },
  })

  const camera = useQrCamera((scanned) => {
    fromCamera.current = true
    lookup.mutate(scanned)
  })

  // Only focus the typed field when the camera is not the thing being used — pulling focus raises the
  // keyboard over the viewfinder.
  useEffect(() => {
    if (camera.status === 'idle') inputRef.current?.focus()
  }, [camera.status])

  const notFound = lookup.error instanceof ApiError && lookup.error.status === 404
  const live = camera.status === 'starting' || camera.status === 'scanning'

  return <>
    <h1 className="text-[22px] font-bold leading-tight">Scan an asset</h1>
    <p className="mt-1 text-[15px] text-slate-500">Point the camera at a label, or type its asset tag or serial number.</p>

    <div className={live ? 'mt-4' : 'hidden'}>
      <div className="relative overflow-hidden rounded-xl border border-slate-200 bg-slate-900 dark:border-slate-800">
        <video ref={camera.videoRef} muted playsInline className="aspect-[3/4] w-full object-cover" />
        {/* A frame to aim with. Purely a sighting aid — jsQR reads the whole frame, not just this box. */}
        <div className="pointer-events-none absolute inset-0 grid place-items-center">
          <div className="size-48 rounded-xl border-2 border-white/80" />
        </div>
        <button
          type="button"
          onClick={camera.stop}
          aria-label="Close camera"
          className="absolute right-2 top-2 grid size-11 place-items-center rounded-lg bg-black/50 text-white"
        ><X size={20} /></button>
      </div>
      <p className="mt-2 text-center text-[13px] text-slate-500">
        {camera.status === 'starting' ? 'Opening the camera…' : 'Hold the label inside the frame.'}
      </p>
    </div>

    {camera.status === 'denied' && <p role="alert" className="mt-4 rounded-xl border border-amber-200 bg-amber-50 p-3 text-[15px] text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
      This browser is not allowed to use the camera. Allow it in Settings, or type the asset tag below.
    </p>}
    {camera.status === 'unavailable' && <p role="alert" className="mt-4 rounded-xl border border-slate-200 bg-white p-3 text-[15px] text-slate-600 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-300">
      No camera is available here. Type the asset tag below instead.
    </p>}

    <form
      className={live ? 'hidden' : 'mt-5'}
      onSubmit={(event) => {
        event.preventDefault()
        const trimmed = code.trim()
        if (!trimmed) return
        fromCamera.current = false
        lookup.mutate(trimmed)
      }}
    >
      <label htmlFor="field-scan-code" className="text-[13px] font-medium text-slate-500">Asset tag or serial</label>
      <input
        id="field-scan-code"
        ref={inputRef}
        value={code}
        onChange={(event) => setCode(event.target.value)}
        autoComplete="off"
        autoCapitalize="characters"
        // 16px minimum, because anything smaller makes iOS Safari zoom the page on focus and the
        // technician then has to pinch back out one-handed.
        className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
      />
      {notFound && <p role="alert" className="mt-2 text-[13px] text-red-600">No asset carries that code.</p>}
      {lookup.isError && !notFound && <p role="alert" className="mt-2 text-[13px] text-red-600">{(lookup.error as Error).message}</p>}

      <FieldActionBar>
        <Button type="button" className="h-12 w-full text-[15px]" onClick={() => {
          fromCamera.current = true
          void camera.start()
        }}>
          <Camera size={18} />Scan with camera
        </Button>
        <Button type="submit" variant="secondary" className="h-12 w-full text-[15px]" disabled={!code.trim() || lookup.isPending}>
          <ScanLine size={18} />{lookup.isPending ? 'Looking up…' : 'Find asset'}
        </Button>
      </FieldActionBar>
    </form>
  </>
}
