import { useMutation } from '@tanstack/react-query'
import { ArrowRight, ScanLine } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { assetsApi, ciTypeLabel, type Ci } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { ciLifecycleLabel, ciLifecycleTone } from './lifecycle'

/**
 * The mobile end of a printed label. A phone camera scanning the QR opens the asset page directly —
 * the code carries its URL — so this page exists for the other half: a handheld or wedge scanner that
 * types an asset tag or serial, and anyone reading a code off a sticker by hand.
 */
const recentKey = 'scan.recent'
const recentLimit = 8

function readRecent(): Ci[] {
  try {
    const stored: unknown = JSON.parse(sessionStorage.getItem(recentKey) ?? '[]')
    return Array.isArray(stored) ? stored as Ci[] : []
  } catch {
    // A malformed or unavailable store is not worth a broken page — the list is a convenience.
    return []
  }
}

function writeRecent(ci: Ci): Ci[] {
  const next = [ci, ...readRecent().filter((item) => item.id !== ci.id)].slice(0, recentLimit)
  try {
    sessionStorage.setItem(recentKey, JSON.stringify(next))
  } catch {
    // Private-mode quota failures leave the in-memory list correct for this render.
  }
  return next
}

export function ScanPage() {
  const navigate = useNavigate()
  const inputRef = useRef<HTMLInputElement>(null)
  const [code, setCode] = useState('')
  // Kept in session storage rather than component state: a successful scan navigates away, which
  // unmounts this page, so anything held in a hook would be gone before it could be shown.
  const [recent, setRecent] = useState<Ci[]>(readRecent)

  // A wedge scanner types its code and presses Enter at whatever moment the trigger is pulled, so the
  // field has to be holding focus before anyone thinks to tap it.
  useEffect(() => inputRef.current?.focus(), [])

  const lookup = useMutation({
    mutationFn: (scanned: string) => assetsApi.lookupCi(scanned),
    onSuccess: (ci) => {
      setCode('')
      setRecent(writeRecent(ci))
      navigate(`/assets/${ci.id}`)
    },
  })

  const notFound = lookup.error instanceof ApiError && lookup.error.status === 404

  return <div className="mx-auto max-w-xl space-y-6">
    <div>
      <h1 className="text-[28px] font-bold">Scan an asset</h1>
      <p className="mt-1 text-sm text-slate-500">
        Point a phone camera at a label&apos;s QR to open its asset page. Use the box below for a handheld
        scanner, or to look an asset up by its printed asset tag or serial number.
      </p>
      {/* Counting a site is the same gesture aimed at a list, so the two belong beside each other. */}
      <p className="mt-2 text-sm text-slate-500">
        Counting a whole site? <Link to="/audits" className="font-medium text-blue-600 hover:underline">Start a physical audit</Link> and
        every scan is confirmed against what the CMDB expects to be there.
      </p>
    </div>

    <form className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); if (code.trim()) lookup.mutate(code.trim()) }}>
      <label htmlFor="scan-code" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Asset tag, serial number, or scanned code</label>
      <div className="flex gap-2">
        <input id="scan-code" ref={inputRef} value={code} autoComplete="off" autoCapitalize="off" autoCorrect="off"
          spellCheck={false} enterKeyHint="go" placeholder="LT-00421"
          className="input h-12 flex-1 text-base"
          onChange={(event) => { setCode(event.target.value); if (lookup.isError) lookup.reset() }} />
        <Button type="submit" className="h-12 px-5" disabled={!code.trim() || lookup.isPending}>
          {lookup.isPending ? 'Looking up…' : <><ArrowRight size={18} />Find</>}
        </Button>
      </div>
      {lookup.isError && <p role="alert" className="mt-2 text-xs text-red-600">
        {notFound
          ? `Nothing in the CMDB carries the code “${code.trim()}”. Check the label, or search the asset list.`
          : lookup.error.message}
      </p>}
    </form>

    {recent.length > 0 && <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <h2 className="border-b border-slate-200 p-4 font-semibold dark:border-slate-800">Scanned in this session</h2>
      <ul>
        {recent.map((ci) => <li key={ci.id} className="border-b border-slate-200 last:border-0 dark:border-slate-800">
          <Link to={`/assets/${ci.id}`} className="flex items-center gap-3 p-4 hover:bg-slate-50 dark:hover:bg-slate-800/50">
            <span className="min-w-0 flex-1">
              <span className="block truncate font-medium">{ci.name}</span>
              <span className="block truncate text-[13px] text-slate-500">
                {ciTypeLabel(ci.type)}{ci.assetTag && <> · <span className="font-mono">{ci.assetTag}</span></>}
              </span>
            </span>
            <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(ci.lifecycleState)}`}>{ciLifecycleLabel(ci.lifecycleState)}</span>
          </Link>
        </li>)}
      </ul>
    </section>}

    {recent.length === 0 && !lookup.isError && <div className="grid place-items-center rounded-xl border border-dashed border-slate-200 p-8 text-center dark:border-slate-800">
      <span className="grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/10"><ScanLine /></span>
      <p className="mt-3 text-sm text-slate-500">Assets you look up here will be listed for the rest of this session.</p>
    </div>}
  </div>
}
