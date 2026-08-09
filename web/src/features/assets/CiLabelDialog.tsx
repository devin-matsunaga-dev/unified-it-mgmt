import { useMutation } from '@tanstack/react-query'
import { Download, Printer } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { assetsApi, ciLabelSizes, type Ci, type CiLabelSize } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { openFile, saveFile } from './labelFiles'

/**
 * QR labels for one CI or for a whole selection. The code encodes the asset's own URL, so scanning it
 * with a phone camera opens that asset's page — nothing has to be typed and no app is involved.
 */
export function CiLabelDialog({ selection, onClose }: { selection: Ci[]; onClose: () => void }) {
  const [size, setSize] = useState<CiLabelSize>('Standard')

  const generate = useMutation({
    mutationFn: (action: 'open' | 'download') => (selection.length === 1
      ? assetsApi.getCiLabel(selection[0].id, size)
      : assetsApi.getCiLabelSheet(selection.map((ci) => ci.id), size)
    ).then((file) => ({ file, action })),
    onSuccess: ({ file, action }) => {
      if (action === 'download') {
        saveFile(file)
        return
      }

      if (!openFile(file)) {
        toast.error('Your browser blocked the new tab. Use Download instead.')
      }
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (selection.length === 0) return null

  const single = selection.length === 1
  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label="Print asset labels">
    <div className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-lg font-semibold">{single ? 'Print label' : `Print ${selection.length} labels`}</h2>
      <p className="mt-1 text-sm text-slate-500">
        {single
          ? 'One label on a page cut to its own size, for a label printer.'
          : 'An A4 sheet of labels with cut guides, in the order they were selected.'}
        {' '}Each QR opens the asset&apos;s page when scanned.
      </p>

      <fieldset className="mt-5">
        <legend className="mb-1.5 text-[13px] font-medium text-slate-600 dark:text-slate-300">Label size</legend>
        <div className="space-y-2">
          {ciLabelSizes.map((option) => <label key={option.value} className={cn('flex cursor-pointer items-center gap-3 rounded-lg border p-3 text-sm', size === option.value ? 'border-blue-600 bg-blue-50 dark:bg-blue-500/10' : 'border-slate-200 dark:border-slate-700')}>
            <input type="radio" name="label-size" value={option.value} checked={size === option.value} onChange={() => setSize(option.value)} />
            <span><span className="font-medium">{option.label}</span><span className="ml-2 text-slate-500">{option.hint}</span></span>
          </label>)}
        </div>
      </fieldset>

      {!single && <p className="mt-4 text-[13px] text-slate-500">
        Printing {selection.length} labels. Set the printer to 100% scale — “fit to page” resizes the sheet and the labels stop lining up with the stock.
      </p>}

      <div className="mt-6 flex flex-wrap justify-end gap-2">
        <Button type="button" variant="secondary" disabled={generate.isPending} onClick={onClose}>Close</Button>
        <Button type="button" variant="secondary" disabled={generate.isPending} onClick={() => generate.mutate('download')}>
          <Download size={16} />Download PDF
        </Button>
        <Button type="button" disabled={generate.isPending} onClick={() => generate.mutate('open')}>
          <Printer size={16} />{generate.isPending ? 'Generating…' : 'Open to print'}
        </Button>
      </div>
    </div>
  </div>
}
