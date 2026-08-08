import { useEffect, useState } from 'react'
import { contractTypes, type Contract, type ContractInput, type ContractType, type Vendor } from '../../api/contracts'
import type { DirectoryUser } from '../../api/directory'
import { Button } from '../../components/ui/Button'

/** Create or edit one contract. Dates are calendar dates, so the inputs are plain date pickers. */
export function ContractFormDialog({ open, contract, vendors, users, pending, error, onClose, onSubmit }: {
  open: boolean
  contract: Contract | null
  vendors: Vendor[]
  users: DirectoryUser[]
  pending: boolean
  error?: string
  onClose: () => void
  onSubmit: (input: ContractInput) => Promise<void>
}) {
  const [form, setForm] = useState<ContractInput>(emptyContract())

  useEffect(() => {
    if (!open) return
    setForm(contract ? {
      vendorId: contract.vendorId,
      contractNumber: contract.contractNumber,
      name: contract.name,
      type: contract.type,
      startDate: contract.startDate,
      endDate: contract.endDate,
      autoRenews: contract.autoRenews,
      cost: contract.cost,
      currency: contract.currency,
      ownerUserId: contract.ownerUserId,
      notes: contract.notes,
    } : emptyContract())
  }, [open, contract])

  if (!open) return null

  const set = <K extends keyof ContractInput>(key: K, value: ContractInput[K]) =>
    setForm((current) => ({ ...current, [key]: value }))
  const invalidDates = Boolean(form.startDate && form.endDate && form.endDate < form.startDate)
  const complete = form.vendorId && form.contractNumber.trim() && form.name.trim() && form.startDate && form.endDate

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={contract ? `Edit ${contract.name}` : 'New contract'}>
    <form
      className="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={async (event) => { event.preventDefault(); if (!invalidDates) await onSubmit(form) }}
    >
      <h2 className="text-lg font-semibold">{contract ? 'Edit contract' : 'New contract'}</h2>
      <p className="mt-1 text-sm text-slate-500">Renewal notices are raised 30 and 7 days before the end date, and on the day itself.</p>

      <div className="mt-5 grid gap-4 sm:grid-cols-2">
        <Field label="Vendor" htmlFor="contract-vendor">
          <select id="contract-vendor" required className="input h-11" value={form.vendorId} onChange={(event) => set('vendorId', event.target.value)}>
            <option value="">Choose a vendor…</option>
            {vendors.map((vendor) => <option key={vendor.id} value={vendor.id}>{vendor.name}</option>)}
          </select>
        </Field>
        <Field label="Contract number" htmlFor="contract-number">
          <input id="contract-number" required maxLength={100} className="input h-11" value={form.contractNumber} onChange={(event) => set('contractNumber', event.target.value)} />
        </Field>
        <Field label="Name" htmlFor="contract-name">
          <input id="contract-name" required maxLength={200} className="input h-11" value={form.name} onChange={(event) => set('name', event.target.value)} />
        </Field>
        <Field label="Type" htmlFor="contract-type">
          <select id="contract-type" className="input h-11" value={form.type} onChange={(event) => set('type', event.target.value as ContractType)}>
            {contractTypes.map((type) => <option key={type} value={type}>{type}</option>)}
          </select>
        </Field>
        <Field label="Start date" htmlFor="contract-start">
          <input id="contract-start" type="date" required className="input h-11" value={form.startDate} onChange={(event) => set('startDate', event.target.value)} />
        </Field>
        <Field label="End date" htmlFor="contract-end" error={invalidDates ? 'The end date cannot be before the start date.' : undefined}>
          <input id="contract-end" type="date" required className="input h-11" value={form.endDate} onChange={(event) => set('endDate', event.target.value)} />
        </Field>
        <Field label="Cost" htmlFor="contract-cost">
          <input id="contract-cost" type="number" min={0} step="0.01" className="input h-11 tabular-nums" value={form.cost ?? ''} onChange={(event) => set('cost', event.target.value === '' ? null : Number(event.target.value))} />
        </Field>
        <Field label="Currency" htmlFor="contract-currency">
          <input id="contract-currency" maxLength={3} placeholder="USD" className="input h-11 uppercase" value={form.currency ?? ''} onChange={(event) => set('currency', event.target.value || null)} />
        </Field>
        <Field label="Internal owner" htmlFor="contract-owner">
          <select id="contract-owner" className="input h-11" value={form.ownerUserId ?? ''} onChange={(event) => set('ownerUserId', event.target.value || null)}>
            <option value="">Nobody — notices go to the asset mailbox</option>
            {users.map((user) => <option key={user.id} value={user.id}>{user.displayName}</option>)}
          </select>
        </Field>
        <label className="flex items-center gap-2 self-end pb-2 text-sm text-slate-600 dark:text-slate-300">
          <input type="checkbox" checked={form.autoRenews} onChange={(event) => set('autoRenews', event.target.checked)} />
          Renews automatically
        </label>
      </div>

      <div className="mt-4">
        <Field label="Notes" htmlFor="contract-notes">
          <textarea id="contract-notes" rows={3} maxLength={2000} className="input min-h-20 py-2" value={form.notes ?? ''} onChange={(event) => set('notes', event.target.value || null)} />
        </Field>
      </div>

      {error && <p role="alert" className="mt-4 text-xs text-red-600">{error}</p>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={pending || invalidDates || !complete}>{pending ? 'Saving…' : contract ? 'Save contract' : 'Create contract'}</Button>
      </div>
    </form>
  </div>
}

function emptyContract(): ContractInput {
  return {
    vendorId: '',
    contractNumber: '',
    name: '',
    type: 'Support',
    startDate: '',
    endDate: '',
    autoRenews: false,
    cost: null,
    currency: null,
    ownerUserId: null,
    notes: null,
  }
}

function Field({ label, htmlFor, error, children }: { label: string; htmlFor: string; error?: string; children: React.ReactNode }) {
  return <div>
    <label htmlFor={htmlFor} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">{label}</label>
    {children}
    {error && <p className="mt-1 text-xs text-red-600">{error}</p>}
  </div>
}
