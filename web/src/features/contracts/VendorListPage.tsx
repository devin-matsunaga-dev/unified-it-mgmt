import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Building2, Pencil, Plus, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { contractsApi, type Vendor, type VendorInput } from '../../api/contracts'
import { Button } from '../../components/ui/Button'

/** The suppliers contracts are held with. A vendor with contracts cannot be deleted (409). */
export function VendorListPage() {
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<Vendor | null>(null)
  const [dialogOpen, setDialogOpen] = useState(false)

  const vendors = useQuery({ queryKey: ['vendors'], queryFn: () => contractsApi.listVendors() })

  const save = useMutation({
    mutationFn: (input: VendorInput) => editing
      ? contractsApi.updateVendor(editing.id, { ...input, isActive: editing.isActive })
      : contractsApi.createVendor(input),
    onSuccess: async (vendor) => {
      await queryClient.invalidateQueries({ queryKey: ['vendors'] })
      toast.success(`${vendor.name} ${editing ? 'updated' : 'created'}`)
      setDialogOpen(false)
      setEditing(null)
    },
  })

  const remove = useMutation({
    mutationFn: (vendor: Vendor) => contractsApi.deleteVendor(vendor.id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['vendors'] })
      toast.success('Vendor deleted')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const items = vendors.data?.items ?? []

  return <div className="space-y-6">
    <Link to="/contracts" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to contracts</Link>

    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[28px] font-bold">Vendors</h1>
        <p className="mt-1 text-sm text-slate-500">Who the organisation buys from and holds agreements with.</p>
      </div>
      <Button className="sm:ml-auto" onClick={() => { setEditing(null); setDialogOpen(true) }}><Plus size={18} />New vendor</Button>
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      {vendors.isLoading ? <div aria-label="Loading vendors" className="space-y-px p-4">{Array.from({ length: 4 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
        : vendors.isError ? <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
            <h2 className="font-semibold">Vendors could not be loaded</h2>
            <p className="mt-1 text-sm text-slate-500">{vendors.error instanceof ApiError ? vendors.error.message : 'Try again in a moment.'}</p>
            <Button className="mt-4" variant="secondary" onClick={() => void vendors.refetch()}>Try again</Button>
          </div></div>
        : items.length === 0 ? <div className="grid min-h-64 place-items-center p-8 text-center"><div>
            <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><Building2 /></span>
            <h2 className="mt-3 font-semibold">No vendors yet</h2>
            <p className="mt-1 text-sm text-slate-500">A contract needs a vendor, so start here.</p>
            <Button className="mt-4" onClick={() => { setEditing(null); setDialogOpen(true) }}>New vendor</Button>
          </div></div>
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[800px] text-left text-sm">
              <thead><tr>
                {['Name', 'Contact', 'Email', 'Phone', 'Contracts', ''].map((header) => <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {items.map((vendor) => <tr key={vendor.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4 font-medium text-slate-900 dark:text-slate-100">{vendor.name}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{vendor.contactName ?? '—'}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{vendor.contactEmail ?? '—'}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{vendor.contactPhone ?? '—'}</td>
                  <td className="h-12 px-4 text-right tabular-nums text-slate-600 dark:text-slate-300">{vendor.contractCount}</td>
                  <td className="h-12 px-4 text-right whitespace-nowrap">
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" onClick={() => { setEditing(vendor); setDialogOpen(true) }}><Pencil size={15} />Edit</Button>
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" disabled={remove.isPending}
                      onClick={() => { if (window.confirm(`Delete ${vendor.name}?`)) remove.mutate(vendor) }}>
                      <Trash2 size={15} />Delete
                    </Button>
                  </td>
                </tr>)}
              </tbody>
            </table>
          </div>}
    </section>

    <VendorFormDialog open={dialogOpen} vendor={editing} pending={save.isPending}
      error={save.error instanceof Error ? save.error.message : undefined}
      onClose={() => { if (!save.isPending) { setDialogOpen(false); setEditing(null); save.reset() } }}
      onSubmit={async (input) => { await save.mutateAsync(input) }} />
  </div>
}

function VendorFormDialog({ open, vendor, pending, error, onClose, onSubmit }: {
  open: boolean
  vendor: Vendor | null
  pending: boolean
  error?: string
  onClose: () => void
  onSubmit: (input: VendorInput) => Promise<void>
}) {
  const [form, setForm] = useState<VendorInput>(emptyVendor())

  useEffect(() => {
    if (!open) return
    setForm(vendor ? {
      name: vendor.name,
      contactName: vendor.contactName,
      contactEmail: vendor.contactEmail,
      contactPhone: vendor.contactPhone,
      website: vendor.website,
      notes: vendor.notes,
    } : emptyVendor())
  }, [open, vendor])

  if (!open) return null

  const set = <K extends keyof VendorInput>(key: K, value: VendorInput[K]) =>
    setForm((current) => ({ ...current, [key]: value }))

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={vendor ? `Edit ${vendor.name}` : 'New vendor'}>
    <form className="w-full max-w-xl rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={async (event) => { event.preventDefault(); await onSubmit(form) }}>
      <h2 className="text-lg font-semibold">{vendor ? 'Edit vendor' : 'New vendor'}</h2>

      <div className="mt-5 grid gap-4 sm:grid-cols-2">
        <Field label="Name" htmlFor="vendor-name">
          <input id="vendor-name" required maxLength={200} className="input h-11" value={form.name} onChange={(event) => set('name', event.target.value)} />
        </Field>
        <Field label="Contact" htmlFor="vendor-contact">
          <input id="vendor-contact" maxLength={200} className="input h-11" value={form.contactName ?? ''} onChange={(event) => set('contactName', event.target.value || null)} />
        </Field>
        <Field label="Email" htmlFor="vendor-email">
          <input id="vendor-email" type="email" maxLength={320} className="input h-11" value={form.contactEmail ?? ''} onChange={(event) => set('contactEmail', event.target.value || null)} />
        </Field>
        <Field label="Phone" htmlFor="vendor-phone">
          <input id="vendor-phone" maxLength={50} className="input h-11" value={form.contactPhone ?? ''} onChange={(event) => set('contactPhone', event.target.value || null)} />
        </Field>
        <Field label="Website" htmlFor="vendor-website">
          <input id="vendor-website" maxLength={500} className="input h-11" value={form.website ?? ''} onChange={(event) => set('website', event.target.value || null)} />
        </Field>
      </div>

      <div className="mt-4">
        <Field label="Notes" htmlFor="vendor-notes">
          <textarea id="vendor-notes" rows={3} maxLength={2000} className="input min-h-20 py-2" value={form.notes ?? ''} onChange={(event) => set('notes', event.target.value || null)} />
        </Field>
      </div>

      {error && <p role="alert" className="mt-4 text-xs text-red-600">{error}</p>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={pending || !form.name.trim()}>{pending ? 'Saving…' : vendor ? 'Save vendor' : 'Create vendor'}</Button>
      </div>
    </form>
  </div>
}

function emptyVendor(): VendorInput {
  return { name: '', contactName: null, contactEmail: null, contactPhone: null, website: null, notes: null }
}

function Field({ label, htmlFor, children }: { label: string; htmlFor: string; children: React.ReactNode }) {
  return <div>
    <label htmlFor={htmlFor} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">{label}</label>
    {children}
  </div>
}
