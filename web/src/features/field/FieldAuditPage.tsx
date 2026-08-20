import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Camera, Check, ChevronLeft, CircleSlash, X } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { reconciliationApi, unexpectedReasonLabel, type AuditScan } from '../../api/reconciliation'
import { Button } from '../../components/ui/Button'
import { FieldActionBar } from '../../layout/FieldShell'
import { cn } from '../../lib/utils'
import { useQrCamera } from './useQrCamera'

/**
 * Walking a stock count. Unlike every other field screen this one does not navigate on a successful
 * scan — the job is label after label, so the camera stays live and each result lands in a list under
 * the viewfinder. A technician holding a phone in one hand and a box in the other should be able to
 * do a whole rack without touching the screen.
 */
type Outcome =
  | { kind: 'counted'; scan: AuditScan }
  | { kind: 'already'; scan: AuditScan }
  | { kind: 'unexpected'; scan: AuditScan }
  | { kind: 'unknown'; code: string }
  | { kind: 'error'; message: string }

function describe(scan: AuditScan): Outcome {
  if (!scan.expected) return { kind: 'unexpected', scan }
  return scan.alreadyScanned ? { kind: 'already', scan } : { kind: 'counted', scan }
}

export function FieldAuditPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  // Newest first, and capped: a long count would otherwise grow an unbounded list on a phone that has
  // to stay responsive while decoding video.
  const [results, setResults] = useState<Outcome[]>([])

  const session = useQuery({
    queryKey: ['audit-session', id],
    queryFn: () => reconciliationApi.getAuditSession(id),
    enabled: Boolean(id),
  })

  const record = useMutation({
    mutationFn: (code: string) => reconciliationApi.recordAuditScan(id, { code }),
    onSuccess: async (scan) => {
      setResults((current) => [describe(scan), ...current].slice(0, 25))
      // Counts come from the server rather than being incremented here: two people can walk one rack,
      // and a number this phone worked out on its own would drift from the one the report shows.
      await queryClient.invalidateQueries({ queryKey: ['audit-session', id] })
    },
    onError: (error: Error, code) => {
      const outcome: Outcome = error instanceof ApiError && error.status === 404
        ? { kind: 'unknown', code }
        : { kind: 'error', message: error.message }
      setResults((current) => [outcome, ...current].slice(0, 25))
    },
  })

  const camera = useQrCamera((code) => record.mutate(code), { continuous: true })
  const live = camera.status === 'starting' || camera.status === 'scanning'

  const counts = session.data

  return <>
    <Link to="/field/audits" className="inline-flex h-11 items-center gap-1 text-[15px] font-medium text-blue-600">
      <ChevronLeft size={18} />Counts
    </Link>
    <h1 className="mt-1 truncate text-[22px] font-bold leading-tight">{counts?.name ?? 'Stock count'}</h1>

    {counts && <div className="mt-3 grid grid-cols-3 gap-2">
      <Tally label="Counted" value={counts.scannedCount} of={counts.expectedCount} />
      <Tally label="Still owed" value={counts.unscannedCount} tone={counts.unscannedCount > 0 ? 'warn' : 'ok'} />
      <Tally label="Unexpected" value={counts.unexpectedCount} tone={counts.unexpectedCount > 0 ? 'warn' : 'ok'} />
    </div>}

    <div className={live ? 'mt-4' : 'hidden'}>
      <div className="relative overflow-hidden rounded-xl border border-slate-200 bg-slate-900 dark:border-slate-800">
        <video ref={camera.videoRef} muted playsInline className="aspect-square w-full object-cover" />
        <div className="pointer-events-none absolute inset-0 grid place-items-center">
          <div className="size-40 rounded-xl border-2 border-white/80" />
        </div>
        <button
          type="button"
          onClick={camera.stop}
          aria-label="Stop scanning"
          className="absolute right-2 top-2 grid size-11 place-items-center rounded-lg bg-black/50 text-white"
        ><X size={20} /></button>
      </div>
      <p className="mt-2 text-center text-[13px] text-slate-500">
        {camera.status === 'starting' ? 'Opening the camera…' : 'Keep going — each label counts itself.'}
      </p>
    </div>

    {camera.status === 'denied' && <p role="alert" className="mt-4 rounded-xl border border-amber-200 bg-amber-50 p-3 text-[15px] text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
      This browser is not allowed to use the camera. Allow it in Settings to walk a count.
    </p>}
    {camera.status === 'unavailable' && <p role="alert" className="mt-4 rounded-xl border border-slate-200 bg-white p-3 text-[15px] text-slate-600 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-300">
      No camera is available here, so this device cannot walk a count.
    </p>}

    {results.length > 0 && <ul className="mt-4 space-y-2" aria-label="Scanned just now">
      {results.map((result, index) => <ResultRow key={`${index}-${resultKey(result)}`} result={result} />)}
    </ul>}

    <FieldActionBar>
      {!live && <Button className="h-12 w-full text-[15px]" onClick={() => void camera.start()}>
        <Camera size={18} />{results.length > 0 ? 'Keep scanning' : 'Start scanning'}
      </Button>}
    </FieldActionBar>
  </>
}

function resultKey(result: Outcome) {
  if (result.kind === 'unknown') return result.code
  return result.kind === 'error' ? result.message : result.scan.id
}

function Tally({ label, value, of, tone = 'plain' }: {
  label: string
  value: number
  of?: number
  tone?: 'plain' | 'ok' | 'warn'
}) {
  return <div className="rounded-xl border border-slate-200 bg-white p-3 text-center dark:border-slate-800 dark:bg-slate-900">
    <p className={cn('text-[22px] font-bold tabular-nums',
      tone === 'warn' && 'text-amber-600 dark:text-amber-400',
      tone === 'ok' && 'text-green-600 dark:text-green-400')}>
      {value}{of === undefined ? '' : <span className="text-[15px] font-medium text-slate-400">/{of}</span>}
    </p>
    <p className="mt-0.5 text-[13px] text-slate-500">{label}</p>
  </div>
}

function ResultRow({ result }: { result: Outcome }) {
  const { icon, tone, title, detail } = presentation(result)
  return <li className={cn('flex items-start gap-3 rounded-xl border p-3', tone)}>
    <span className="mt-0.5 shrink-0">{icon}</span>
    <span className="min-w-0">
      <span className="block truncate text-[15px] font-medium">{title}</span>
      {detail && <span className="mt-0.5 block text-[13px] opacity-80">{detail}</span>}
    </span>
  </li>
}

function presentation(result: Outcome) {
  switch (result.kind) {
    case 'counted':
      return {
        icon: <Check size={18} />,
        tone: 'border-green-200 bg-green-50 text-green-800 dark:border-green-500/30 dark:bg-green-500/10 dark:text-green-300',
        title: result.scan.ciName,
        detail: result.scan.assetTag,
      }
    case 'already':
      return {
        icon: <Check size={18} />,
        tone: 'border-slate-200 bg-white text-slate-600 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-300',
        title: result.scan.ciName,
        detail: 'Already counted',
      }
    case 'unexpected':
      // Recorded, not refused — the server accepts it and flags why. A technician who finds something
      // that should not be here has found the most useful thing a count can produce.
      return {
        icon: <AlertTriangle size={18} />,
        tone: 'border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300',
        title: result.scan.ciName,
        detail: `Counted, but ${unexpectedReasonLabel(result.scan.unexpectedReason ?? 'DifferentSite').toLowerCase()}`,
      }
    case 'unknown':
      return {
        icon: <CircleSlash size={18} />,
        tone: 'border-red-200 bg-red-50 text-red-800 dark:border-red-500/30 dark:bg-red-500/10 dark:text-red-300',
        title: 'No asset carries that code',
        detail: result.code,
      }
    default:
      return {
        icon: <CircleSlash size={18} />,
        tone: 'border-red-200 bg-red-50 text-red-800 dark:border-red-500/30 dark:bg-red-500/10 dark:text-red-300',
        title: 'That scan did not save',
        detail: result.message,
      }
  }
}
