import { useMutation, useQuery } from '@tanstack/react-query'
import { Camera, ChevronRight, PackagePlus, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { assetsApi } from '../../api/assets'
import { cn } from '../../lib/utils'
import { ciLifecycleLabel, ciLifecycleTone } from '../assets/lifecycle'
import { useDebounced } from './useDebounced'
import { Button } from '../../components/ui/Button'
import { FieldActionBar } from '../../layout/FieldShell'
import { useQrCamera } from './useQrCamera'

/**
 * Where "Scan next" goes. The camera reads the QR in-app so a technician working through a stockroom
 * never leaves the page; the typed field stays for a sticker too scuffed to read, a handheld wedge
 * scanner, and the tag someone already knows by heart.
 *
 * A decoded QR is the label's full URL, which the server's lookup already understands (CiLabelCodes
 * .TryReadCiId), so a camera read resolves to one asset and goes straight there.
 *
 * Typing is a different act and gets different behaviour: a technician holding a device usually has
 * a partial tag, a smudged digit or a number they half remember, so the field searches as they type
 * and narrows. Nothing to press — pressing a button to find out you mistyped is a slow way to learn
 * it, and on a phone the button is also the thing the keyboard is covering.
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

  const term = useDebounced(code.trim())
  // Two characters, because one matches most of the estate and the list would be noise. The server
  // searches name, asset tag and serial together (CiService), which is what makes a half-remembered
  // anything a usable starting point.
  const searchable = term.length >= 2
  const results = useQuery({
    queryKey: ['cis', 'field-search', term],
    queryFn: () => assetsApi.listCis({ search: term, pageSize: 10 }),
    enabled: searchable && !live,
  })
  const found = results.data?.items ?? []

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

    <div className={live ? 'hidden' : 'mt-5'}>
      <label htmlFor="field-scan-code" className="text-[13px] font-medium text-slate-500">
        Search by name, asset tag or serial
      </label>
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
      {/* Results narrow as the field fills. Rendered under the input rather than in a sheet so the
          keyboard stays up and the technician can keep typing to narrow further. */}
      {searchable && <>
        {results.isLoading && <p className="mt-3 text-[13px] text-slate-500">Searching…</p>}

        {!results.isLoading && found.length > 0 && <ul className="mt-3 space-y-2" aria-label="Matching assets">
          {found.map((ci) => <li key={ci.id}>
            <button
              type="button"
              onClick={() => navigate(`/field/ci/${ci.id}`)}
              className="flex min-h-[68px] w-full items-center gap-3 rounded-xl border border-slate-200 bg-white p-3 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-800 dark:bg-slate-900"
            >
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[15px] font-medium">{ci.name}</span>
                <span className="mt-0.5 block truncate text-[13px] tabular-nums text-slate-500">
                  {[ci.assetTag, ci.serialNumber].filter(Boolean).join(' · ') || 'No tag or serial'}
                </span>
              </span>
              <span className={cn('shrink-0 rounded-md px-2 py-0.5 text-xs font-medium', ciLifecycleTone(ci.lifecycleState))}>
                {ciLifecycleLabel(ci.lifecycleState)}
              </span>
              <ChevronRight size={17} className="shrink-0 text-slate-400" />
            </button>
          </li>)}
        </ul>}

        {/* Nothing matched. On a phone the likeliest reason is a device that arrived this morning, so
            the recovery is offered here rather than left to be found. */}
        {!results.isLoading && found.length === 0 && <div className="mt-3">
          <p className="text-[15px] text-slate-500">Nothing matches "{term}".</p>
          <Link
            to={`/field/receive?code=${encodeURIComponent(term)}&checked=1`}
            className="mt-2 flex h-12 items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white text-[15px] font-medium text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300"
          ><PackagePlus size={17} />Receive it as a new asset</Link>
        </div>}

        {results.isError && <p role="alert" className="mt-3 text-[13px] text-red-600">
          {(results.error as Error).message}
        </p>}
      </>}

      {notFound && <p role="alert" className="mt-2 text-[13px] text-red-600">
        That code matches no asset.
      </p>}
      {lookup.isError && !notFound && <p role="alert" className="mt-2 text-[13px] text-red-600">{(lookup.error as Error).message}</p>}

      <FieldActionBar>
        <Button type="button" className="h-12 w-full text-[15px]" onClick={() => {
          fromCamera.current = true
          void camera.start()
        }}>
          <Camera size={18} />Scan with camera
        </Button>
        <Link
          to="/field/receive"
          className="flex h-12 w-full items-center justify-center gap-2 rounded-lg text-[15px] font-medium text-blue-600"
        ><PackagePlus size={17} />Receive a new asset</Link>
      </FieldActionBar>
    </div>
  </>
}
