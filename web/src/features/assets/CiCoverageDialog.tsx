import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import type { Ci } from '../../api/assets'
import { contractsApi, type CiCoverageInput } from '../../api/contracts'
import { Button } from '../../components/ui/Button'

/**
 * Warranty dates and the covering contract for one CI. The payload is a complete statement, so
 * clearing the contract here releases the asset rather than leaving the old one attached.
 */
export function CiCoverageDialog({ ci, onClose }: { ci: Ci | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState<CiCoverageInput>({ contractId: null, purchaseDate: null, warrantyExpiresAt: null })

  useEffect(() => {
    if (!ci) return
    setForm({
      contractId: ci.coverage.contractId,
      purchaseDate: ci.coverage.purchaseDate,
      warrantyExpiresAt: ci.coverage.warrantyExpiresAt,
    })
  }, [ci])

  const contracts = useQuery({
    queryKey: ['contracts', { pageSize: 200 }],
    queryFn: () => contractsApi.listContracts({ pageSize: 200 }),
    enabled: Boolean(ci),
  })

  const save = useMutation({
    mutationFn: () => contractsApi.setCoverage(ci!.id, form),
    onSuccess: async (updated) => {
      await queryClient.invalidateQueries({ queryKey: ['cis'] })
      await queryClient.invalidateQueries({ queryKey: ['contracts'] })
      toast.success(`Coverage saved for ${updated.name}`)
      onClose()
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (!ci) return null

  const frozen = ci.lifecycleState === 'Disposed'
  const invalidDates = Boolean(form.purchaseDate && form.warrantyExpiresAt && form.warrantyExpiresAt < form.purchaseDate)

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={`Warranty and contract for ${ci.name}`}>
    <form className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); if (!invalidDates && !frozen) save.mutate() }}>
      <h2 className="text-lg font-semibold">Warranty &amp; contract</h2>
      <p className="mt-1 text-sm text-slate-500">Renewal notices are raised 30 and 7 days before the warranty ends, and on the day itself.</p>

      <div className="mt-5 space-y-4">
        <div>
          <label htmlFor="coverage-contract" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Covering contract</label>
          <select id="coverage-contract" className="input h-11" disabled={frozen} value={form.contractId ?? ''}
            onChange={(event) => setForm((current) => ({ ...current, contractId: event.target.value || null }))}>
            <option value="">Not covered</option>
            {(contracts.data?.items ?? []).map((contract) => <option key={contract.id} value={contract.id}>{contract.poNumber} — {contract.name} ({contract.vendorName})</option>)}
          </select>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <div>
            <label htmlFor="coverage-purchase" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Purchase date</label>
            <input id="coverage-purchase" type="date" className="input h-11" disabled={frozen} value={form.purchaseDate ?? ''}
              onChange={(event) => setForm((current) => ({ ...current, purchaseDate: event.target.value || null }))} />
          </div>
          <div>
            <label htmlFor="coverage-warranty" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Warranty ends</label>
            <input id="coverage-warranty" type="date" className="input h-11" disabled={frozen} value={form.warrantyExpiresAt ?? ''}
              onChange={(event) => setForm((current) => ({ ...current, warrantyExpiresAt: event.target.value || null }))} />
            {invalidDates && <p className="mt-1 text-xs text-red-600">A warranty cannot end before the asset was bought.</p>}
          </div>
        </div>
        {frozen && <p className="text-sm text-slate-500">A disposed CI is a closed record — its coverage can no longer be changed.</p>}
      </div>

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={save.isPending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={save.isPending || invalidDates || frozen}>{save.isPending ? 'Saving…' : 'Save coverage'}</Button>
      </div>
    </form>
  </div>
}
