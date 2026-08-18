import { useMutation, useQuery } from '@tanstack/react-query'
import { ArrowLeft, CheckCircle2, FileSpreadsheet, Upload } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import {
  assetsApi, ciTypeLabel, ciTypes,
  type CiImportAction, type CiImportColumns, type CiImportReport, type CiImportRowResult,
  type CiImportTarget, type CiImportType, type CiType,
} from '../../api/assets'
import { ApiError } from '../../api/client'
import { Button } from '../../components/ui/Button'

/** Long reports are shown in full up to this many rows; the rest stay in the counts. */
const maximumListedRows = 200

const actionTone: Record<CiImportAction, string> = {
  Create: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400',
  Update: 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400',
  Skip: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
  Error: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400',
}

const actionLabel: Record<CiImportAction, string> = {
  Create: 'Create', Update: 'Update', Skip: 'Skip', Error: 'Error',
}

type Step = 'file' | 'map' | 'preview' | 'done'

export function CiImportWizard() {
  const navigate = useNavigate()
  const [step, setStep] = useState<Step>('file')
  const [file, setFile] = useState<File | null>(null)
  const [type, setType] = useState<CiImportType>('Hardware')
  const [columns, setColumns] = useState<CiImportColumns | null>(null)
  const [mapping, setMapping] = useState<Record<string, string>>({})
  const [report, setReport] = useState<CiImportReport | null>(null)

  // Only used to tell the operator how many CIs of this type already exist before they commit; a mixed
  // file spans all of them, so there is no one count to show.
  const existing = useQuery({
    queryKey: ['cis', { type, pageSize: 1 }],
    queryFn: () => assetsApi.listCis({ type: type as CiType, pageSize: 1 }),
    enabled: type !== 'Mixed',
  })

  const inspect = useMutation({
    mutationFn: () => assetsApi.inspectImport(file!, type),
    onSuccess: (result) => {
      setColumns(result)
      setMapping(result.suggestedMapping)
      setStep('map')
    },
  })

  const preview = useMutation({
    mutationFn: () => assetsApi.previewImport(file!, { type, columns: mapping }),
    onSuccess: (result) => { setReport(result); setStep('preview') },
  })

  // The commit is only reachable from the dry run, which listed every guessed type — that is exactly
  // what the server asks this flag to attest.
  const commit = useMutation({
    mutationFn: () => assetsApi.commitImport(file!, { type, columns: mapping, acceptInferredTypes: true }),
    onSuccess: (result) => {
      setReport(result)
      setStep('done')
      toast.success(`${result.created} created, ${result.updated} updated, ${result.skipped} skipped`)
    },
  })

  const restart = () => {
    setStep('file'); setFile(null); setColumns(null); setMapping({}); setReport(null)
    inspect.reset(); preview.reset(); commit.reset()
  }

  const setColumnFor = (targetKey: string, header: string) => setMapping((current) => {
    const next = { ...current }
    if (header) next[targetKey] = header
    else delete next[targetKey]
    return next
  })

  return <div className="space-y-6">
    <Link to="/assets" className="inline-flex items-center gap-1 text-[13px] text-slate-500 hover:text-blue-600"><ArrowLeft size={15} />Assets</Link>

    <ol className="flex flex-wrap gap-2 text-[13px]">
      {(['file', 'map', 'preview', 'done'] as const).map((name, index) => <li key={name}
        aria-current={step === name ? 'step' : undefined}
        className={`rounded-lg border px-3 py-1.5 ${step === name ? 'border-blue-600 bg-blue-50 font-medium text-blue-700 dark:bg-blue-500/10' : 'border-slate-200 text-slate-500 dark:border-slate-800'}`}>
        {index + 1}. {{ file: 'Choose file', map: 'Map columns', preview: 'Dry run', done: 'Result' }[name]}
      </li>)}
    </ol>

    {step === 'file' && <Card title="Choose a file">
      <div className="grid gap-4 sm:grid-cols-2">
        <label className="block">
          <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">CI type</span>
          <select className="input" aria-label="CI type" value={type} onChange={(event) => setType(event.target.value as CiImportType)}>
            {ciTypes.map((option) => <option key={option} value={option}>{ciTypeLabel(option)}</option>)}
            <option value="Mixed">Mixed — read from a column</option>
          </select>
          <span className="mt-1 block text-xs text-slate-500">{type === 'Mixed'
            ? 'Each row states its own type in a column you map next. Without one, the type is guessed from the columns the row fills and shown in the dry run.'
            : `Every row in the file is imported as this type. ${existing.data ? `${existing.data.total} already registered.` : ''}`}</span>
        </label>
        <label className="block">
          <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">File</span>
          <input type="file" aria-label="File" accept=".csv,.xlsx" className="input h-auto py-2 file:mr-3 file:rounded-md file:border-0 file:bg-slate-100 file:px-3 file:py-1.5 file:text-sm dark:file:bg-slate-800"
            onChange={(event) => setFile(event.target.files?.[0] ?? null)} />
          <span className="mt-1 block text-xs text-slate-500">The first row must name the columns. Up to 5 MB and 5000 rows.</span>
        </label>
      </div>
      <ErrorBanner error={inspect.error} />
      <div className="mt-5 flex gap-2">
        <Button disabled={!file || inspect.isPending} onClick={() => inspect.mutate()}><Upload size={17} />{inspect.isPending ? 'Reading…' : 'Read columns'}</Button>
      </div>
    </Card>}

    {step === 'map' && columns && <>
      <Card title={`Map the columns of ${columns.fileName}`} action={`${columns.rowCount} data rows`}>
        <p className="mb-4 text-sm text-slate-500">Rows are matched to existing CIs by serial number and asset tag, so map at least one of them — otherwise a second run would create everything twice.</p>
        {type === 'Mixed' && <p className="mb-4 text-sm text-slate-500">Every type&rsquo;s columns are offered together: a row fills only the ones its own type has and ignores the rest. Map <strong>CI type</strong> if the file names the type; leave it unmapped and each row&rsquo;s type is guessed from the columns it fills.</p>}
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {columns.targets.map((target) => <MappingField key={target.key} target={target} headers={columns.headers}
            value={mapping[target.key] ?? ''} onChange={(header) => setColumnFor(target.key, header)} />)}
        </div>
        <ErrorBanner error={preview.error} />
        <div className="mt-5 flex gap-2">
          <Button disabled={preview.isPending} onClick={() => preview.mutate()}>{preview.isPending ? 'Checking…' : 'Run the dry run'}</Button>
          <Button variant="secondary" onClick={restart}>Choose another file</Button>
        </div>
      </Card>
      <Card title="First rows of the file">
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead><tr>{columns.headers.map((header) => <th key={header} className="h-9 whitespace-nowrap px-3 text-[13px] font-medium text-slate-500">{header}</th>)}</tr></thead>
            <tbody>
              {columns.sampleRows.map((row, index) => <tr key={index} className="border-t border-slate-200 dark:border-slate-800">
                {row.map((cell, cellIndex) => <td key={cellIndex} className="h-10 whitespace-nowrap px-3 text-slate-600 dark:text-slate-300">{cell || '—'}</td>)}
              </tr>)}
            </tbody>
          </table>
        </div>
      </Card>
    </>}

    {step === 'preview' && report && <Card title="Dry run" action="Nothing has been written yet">
      <Summary report={report} />
      <RowTable report={report} />
      <ErrorBanner error={commit.error} />
      <div className="mt-5 flex gap-2">
        <Button disabled={commit.isPending || report.created + report.updated === 0} onClick={() => commit.mutate()}>
          {commit.isPending ? 'Importing…' : `Import ${report.created + report.updated} rows`}
        </Button>
        <Button variant="secondary" onClick={() => setStep('map')}>Back to mapping</Button>
      </div>
      {report.created + report.updated === 0 && <p className="mt-3 text-[13px] text-slate-500">No row would change anything, so there is nothing to import.</p>}
    </Card>}

    {step === 'done' && report && <Card title="Import complete">
      <div className="mb-4 flex items-center gap-2 text-sm text-green-700 dark:text-green-400"><CheckCircle2 size={18} />{report.created} created, {report.updated} updated, {report.skipped} skipped, {report.failed} failed.</div>
      <Summary report={report} />
      <RowTable report={report} />
      <div className="mt-5 flex gap-2">
        <Button onClick={() => navigate('/assets')}>Back to assets</Button>
        <Button variant="secondary" onClick={restart}>Import another file</Button>
      </div>
    </Card>}
  </div>
}

function MappingField({ target, headers, value, onChange }: {
  target: CiImportTarget
  headers: string[]
  value: string
  onChange: (header: string) => void
}) {
  // A mixed file's columns belong to several types at once, so "required" is only ever true of some of
  // them — the form says which rather than marking the field with a star it cannot honour.
  const requiredFor = target.types?.filter((entry) => entry.isRequired).map((entry) => ciTypeLabel(entry.type)) ?? []
  const offeredTo = target.types?.map((entry) => ciTypeLabel(entry.type)) ?? []
  return <label className="block">
    <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">
      {target.label}{target.isRequired && <span className="ml-1 text-red-600">*</span>}
      {target.kind === 'CustomField' && <span className="ml-1 text-xs font-normal text-slate-400">custom field</span>}
    </span>
    <select className="input" aria-label={target.label} value={value} onChange={(event) => onChange(event.target.value)}>
      <option value="">Not imported</option>
      {headers.map((header) => <option key={header} value={header}>{header}</option>)}
    </select>
    {offeredTo.length > 0 && <span className="mt-1 block text-xs text-slate-500">
      {requiredFor.length > 0 ? `Required for ${requiredFor.join(', ')}` : `Optional for ${offeredTo.join(', ')}`}
      {requiredFor.length > 0 && requiredFor.length < offeredTo.length
        ? `; optional for ${offeredTo.filter((label) => !requiredFor.includes(label)).join(', ')}`
        : ''}
    </span>}
  </label>
}

function Summary({ report }: { report: CiImportReport }) {
  const tiles: [string, number, string][] = [
    ['Create', report.created, actionTone.Create],
    ['Update', report.updated, actionTone.Update],
    ['Skip', report.skipped, actionTone.Skip],
    ['Error', report.failed, actionTone.Error],
  ]
  return <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
    {tiles.map(([label, value, tone]) => <div key={label} className="rounded-xl border border-slate-200 p-4 dark:border-slate-800">
      <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${tone}`}>{label}</span>
      <p className="mt-2 text-[30px] font-bold tabular-nums leading-none">{value}</p>
    </div>)}
  </div>
}

function RowTable({ report }: { report: CiImportReport }) {
  // Errors first: they are the only rows anybody has to act on.
  const rows = [...report.rows].sort((left, right) =>
    Number(right.action === 'Error') - Number(left.action === 'Error') || left.lineNumber - right.lineNumber)
  const listed = rows.slice(0, maximumListedRows)
  // The type column only earns its width on a mixed file — on a single-type import every row says the
  // same thing, which the operator chose two steps ago.
  const showsType = rows.some((row) => row.typeSource && row.typeSource !== 'Fixed')
  const guessed = rows.filter((row) => row.typeSource === 'Inferred').length
  const headers = showsType
    ? ['Line', 'Action', 'Type', 'Name', 'Asset tag', 'Serial', 'Detail']
    : ['Line', 'Action', 'Name', 'Asset tag', 'Serial', 'Detail']
  return <div className="mt-5">
    {showsType && guessed > 0 && report.isDryRun && <p className="mb-3 text-[13px] text-amber-700 dark:text-amber-500">
      {guessed} {guessed === 1 ? 'row has a guessed type' : 'rows have a guessed type'} — a CI&rsquo;s type cannot be
      changed after it is created, so check them before importing.
    </p>}
    <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-800">
      <table className="w-full min-w-[720px] text-left text-sm">
        <thead><tr>{headers.map((header) =>
          <th key={header} className="h-10 px-3 text-[13px] font-medium text-slate-500">{header}</th>)}</tr></thead>
        <tbody>
          {listed.map((row) => <tr key={row.lineNumber} className="border-t border-slate-200 dark:border-slate-800">
            <td className="h-11 px-3 font-mono text-xs text-slate-500 tabular-nums">{row.lineNumber}</td>
            <td className="h-11 px-3"><span className={`rounded-md px-2 py-0.5 text-xs font-medium ${actionTone[row.action]}`}>{actionLabel[row.action]}</span></td>
            {showsType && <td className="h-11 px-3"><RowType row={row} /></td>}
            <td className="h-11 px-3 text-slate-700 dark:text-slate-200">{row.name ?? '—'}</td>
            <td className="h-11 px-3 font-mono text-xs text-slate-500">{row.assetTag ?? '—'}</td>
            <td className="h-11 px-3 font-mono text-xs text-slate-500">{row.serialNumber ?? '—'}</td>
            <td className="h-11 px-3 text-[13px] text-red-600 dark:text-red-400">{row.errors.join(' ')}</td>
          </tr>)}
        </tbody>
      </table>
    </div>
    {rows.length > listed.length && <p className="mt-2 text-[13px] text-slate-500">Showing the first {maximumListedRows} of {rows.length} rows; the counts above cover all of them.</p>}
  </div>
}

function RowType({ row }: { row: CiImportRowResult }) {
  if (!row.type) return <span className="text-[13px] text-slate-400">—</span>
  return <span className="whitespace-nowrap text-slate-700 dark:text-slate-200">
    {ciTypeLabel(row.type)}
    {row.typeSource === 'Inferred' && <span className="ml-1.5 rounded-md bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-500/15 dark:text-amber-500">guessed</span>}
  </span>
}

function Card({ title, action, children }: { title: string; action?: string; children: ReactNode }) {
  return <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
    <div className="mb-4 flex items-center gap-3">
      <span className="grid size-9 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/10"><FileSpreadsheet size={18} /></span>
      <h2 className="text-base font-semibold">{title}</h2>
      {action && <span className="ml-auto text-[13px] text-slate-500">{action}</span>}
    </div>
    {children}
  </section>
}

function ErrorBanner({ error }: { error: unknown }) {
  if (!error) return null
  const fieldErrors = error instanceof ApiError ? Object.values(error.errors ?? {}).flat() : []
  return <div role="alert" className="mt-4 rounded-lg border border-red-200 bg-red-50 p-3 text-[13px] text-red-700 dark:border-red-500/30 dark:bg-red-500/10 dark:text-red-400">
    {fieldErrors.length > 0
      ? <ul className="list-inside list-disc space-y-1">{fieldErrors.map((message) => <li key={message}>{message}</li>)}</ul>
      : (error instanceof Error ? error.message : 'The import could not be completed.')}
  </div>
}
