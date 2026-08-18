import { useMutation } from '@tanstack/react-query'
import { ArrowLeft, CheckCircle2, FileSpreadsheet, Upload } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { softwareApi, type SoftwareImportAction, type SoftwareImportReport } from '../../api/software'
import { Button } from '../../components/ui/Button'

/** Long reports are listed in full up to this many rows; the rest stay in the counts. */
const maximumListedRows = 200

const actionTone: Record<SoftwareImportAction, string> = {
  Create: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400',
  Update: 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400',
  Error: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400',
}

/**
 * Upload an inventory export, see exactly what it will do, then commit it. Two steps rather than the
 * CI importer's four: an inventory file has a known shape, so there is no column mapping to choose.
 */
export function SoftwareImportPage() {
  const navigate = useNavigate()
  const [file, setFile] = useState<File | null>(null)
  const [report, setReport] = useState<SoftwareImportReport | null>(null)

  const preview = useMutation({
    mutationFn: (chosen: File) => softwareApi.previewImport(chosen),
    onSuccess: setReport,
  })

  const commit = useMutation({
    mutationFn: (chosen: File) => softwareApi.commitImport(chosen),
    onSuccess: (result) => {
      setReport(result)
      toast.success(`${result.created} install${result.created === 1 ? '' : 's'} recorded, ${result.updated} refreshed.`)
    },
  })

  const committed = report !== null && !report.isDryRun

  return <div className="space-y-6">
    <div>
      <Link to="/software" className="inline-flex items-center gap-1 text-[13px] text-slate-500 hover:text-blue-600"><ArrowLeft size={15} />Back to software</Link>
      <p className="mt-2 text-sm text-slate-500">Each row is one piece of software on one machine.</p>
    </div>

    <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="font-semibold">The file</h2>
      <p className="mt-1 text-sm text-slate-500">
        The first row must name the columns. A machine column — <code>asset tag</code>, <code>serial number</code>{' '}
        or <code>hostname</code> — and a <code>software</code> column are required; <code>publisher</code>,{' '}
        <code>version</code> and <code>installed on</code> are optional. Up to 5 MB and 5000 rows.
      </p>
      <div className="mt-4 flex flex-wrap items-end gap-3">
        <label className="block min-w-72 flex-1">
          <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">File</span>
          <input type="file" aria-label="File" accept=".csv,.xlsx"
            className="input h-auto py-2 file:mr-3 file:rounded-md file:border-0 file:bg-slate-100 file:px-3 file:py-1.5 file:text-sm dark:file:bg-slate-800"
            onChange={(event) => { setFile(event.target.files?.[0] ?? null); setReport(null); preview.reset(); commit.reset() }} />
        </label>
        <Button disabled={!file || preview.isPending} onClick={() => file && preview.mutate(file)}>
          <Upload size={18} />{preview.isPending ? 'Reading…' : 'Preview'}
        </Button>
      </div>
      <ErrorBanner error={preview.error ?? commit.error} />
    </section>

    {report && <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 p-4 dark:border-slate-800">
        <h2 className="font-semibold">{report.isDryRun ? 'What this import will do' : 'What this import did'}</h2>
        <span className="text-sm text-slate-500">{report.fileName}</span>
        {committed && <span className="flex items-center gap-1 text-sm text-green-700 dark:text-green-400"><CheckCircle2 size={16} />Committed</span>}
      </div>

      <dl className="grid gap-4 p-4 sm:grid-cols-3 lg:grid-cols-6">
        <Figure label="Rows" value={report.totalRows} />
        <Figure label="New installs" value={report.created} />
        <Figure label="Refreshed" value={report.updated} />
        <Figure label="Failed" value={report.failed} />
        <Figure label="Machines" value={report.machinesMatched} />
        <Figure label="Normalised" value={report.normalised} />
      </dl>

      {report.unrecognisedNames.length > 0 && <div className="border-t border-slate-200 p-4 dark:border-slate-800">
        <h3 className="text-[13px] font-medium text-slate-500">Names the catalogue does not recognise</h3>
        <p className="mt-1 text-sm text-slate-500">
          These are still recorded against their machines. They count toward no product until a rule claims them.
        </p>
        <ul className="mt-2 flex flex-wrap gap-2 text-sm">
          {report.unrecognisedNames.map((name) => <li key={name} className="rounded-md bg-slate-100 px-2 py-0.5 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{name}</li>)}
        </ul>
      </div>}

      <div className="overflow-x-auto border-t border-slate-200 dark:border-slate-800">
        <table className="w-full min-w-[900px] text-left text-sm">
          <thead><tr>{['Line', 'Action', 'Machine', 'Software', 'Version', 'Product', 'Detail'].map((header) =>
            <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}</tr></thead>
          <tbody>
            {report.rows.slice(0, maximumListedRows).map((row) => <tr key={row.lineNumber} className="border-t border-slate-200 dark:border-slate-800">
              <td className="h-11 px-4 tabular-nums text-slate-500">{row.lineNumber}</td>
              <td className="h-11 px-4"><span className={`rounded-md px-2 py-0.5 text-xs font-medium ${actionTone[row.action]}`}>{row.action}</span></td>
              <td className="h-11 px-4 text-slate-600 dark:text-slate-300">{row.ciName ?? row.machine ?? '—'}</td>
              <td className="h-11 px-4 text-slate-600 dark:text-slate-300">{row.softwareName ?? '—'}</td>
              <td className="h-11 px-4 tabular-nums text-slate-600 dark:text-slate-300">{row.version ?? '—'}</td>
              <td className="h-11 px-4 text-slate-600 dark:text-slate-300">{row.productName ?? <span className="text-slate-400">Unrecognised</span>}</td>
              <td className="h-11 px-4 text-red-600">{row.errors.join(' ')}</td>
            </tr>)}
          </tbody>
        </table>
        {report.rows.length > maximumListedRows && <p className="p-4 text-[13px] text-slate-500">
          Showing the first {maximumListedRows} of {report.rows.length} rows.
        </p>}
      </div>

      <div className="flex flex-wrap justify-end gap-2 border-t border-slate-200 p-4 dark:border-slate-800">
        {committed
          ? <Button onClick={() => navigate('/software')}>Back to software</Button>
          : <>
              <Button variant="secondary" onClick={() => { setReport(null); preview.reset() }}>Start again</Button>
              {/* The commit re-sends the same file rather than a parked copy of it, so nothing
                  half-imported is ever stored on the server between the two steps. */}
              <Button disabled={!file || commit.isPending || report.totalRows === report.failed}
                onClick={() => file && commit.mutate(file)}>
                {commit.isPending ? 'Importing…' : `Import ${report.created + report.updated} row${report.created + report.updated === 1 ? '' : 's'}`}
              </Button>
            </>}
      </div>
    </section>}

    {!report && !preview.isPending && <section className="grid min-h-48 place-items-center rounded-xl border border-slate-200 bg-white p-8 text-center dark:border-slate-800 dark:bg-slate-900">
      <div>
        <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><FileSpreadsheet /></span>
        <h2 className="mt-3 font-semibold">Nothing previewed yet</h2>
        <p className="mt-1 text-sm text-slate-500">Choose a file and preview it. Nothing is written until you import.</p>
      </div>
    </section>}
  </div>
}

function Figure({ label, value }: { label: string; value: number }) {
  return <div>
    <dt className="text-[13px] text-slate-500">{label}</dt>
    <dd className="text-xl font-semibold tabular-nums">{value}</dd>
  </div>
}

function ErrorBanner({ error }: { error: Error | null }) {
  if (!error) return null
  return <p role="alert" className="mt-4 text-sm text-red-600">
    {error instanceof ApiError ? error.message : 'The file could not be read.'}
  </p>
}
