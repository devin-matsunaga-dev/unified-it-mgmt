import { useEffect, useState, type ReactNode } from 'react'
import type { LicensePool, LicensePoolInput, SoftwareProduct } from '../../api/software'
import { Button } from '../../components/ui/Button'

/**
 * Create or edit one block of entitlements. Dates are calendar dates — a licence lapses on a day, not
 * at an instant in somebody's timezone — so the inputs are plain date pickers, as WP-2.6's are.
 */
export function LicensePoolDialog({ open, pool, products, pending, error, onClose, onSubmit }: {
  open: boolean
  pool: LicensePool | null
  products: SoftwareProduct[]
  pending: boolean
  error?: string
  onClose: () => void
  onSubmit: (input: LicensePoolInput) => Promise<void>
}) {
  const [form, setForm] = useState<LicensePoolInput>(emptyPool())

  useEffect(() => {
    if (!open) return
    setForm(pool ? {
      productId: pool.productId,
      name: pool.name,
      reference: pool.reference,
      entitlements: pool.entitlements,
      purchaseDate: pool.purchaseDate,
      expiresAt: pool.expiresAt,
      notes: pool.notes,
    } : emptyPool())
  }, [open, pool])

  if (!open) return null

  const set = <K extends keyof LicensePoolInput>(key: K, value: LicensePoolInput[K]) =>
    setForm((current) => ({ ...current, [key]: value }))
  const invalidDates = Boolean(form.purchaseDate && form.expiresAt && form.expiresAt < form.purchaseDate)
  const complete = Boolean(form.productId && form.name.trim() && form.entitlements >= 0)

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={pool ? `Edit ${pool.name}` : 'New licence pool'}>
    <form
      className="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={async (event) => { event.preventDefault(); if (!invalidDates && complete) await onSubmit(form) }}
    >
      <h2 className="text-lg font-semibold">{pool ? 'Edit licence pool' : 'New licence pool'}</h2>
      <p className="mt-1 text-sm text-slate-500">
        Entitlements are counted against the devices the product is installed on. Renewal notices are
        raised 30 and 7 days before the end date, and on the day itself; leave it blank for a perpetual licence.
      </p>

      <div className="mt-5 grid gap-4 sm:grid-cols-2">
        <Field label="Product" htmlFor="pool-product">
          <select id="pool-product" required className="input h-11" value={form.productId} onChange={(event) => set('productId', event.target.value)}>
            <option value="">Choose a product…</option>
            {products.map((product) => <option key={product.id} value={product.id}>{product.publisher} {product.name}</option>)}
          </select>
        </Field>
        <Field label="Pool name" htmlFor="pool-name">
          <input id="pool-name" required maxLength={200} className="input h-11" value={form.name} onChange={(event) => set('name', event.target.value)} />
        </Field>
        <Field label="Entitlements" htmlFor="pool-entitlements">
          <input id="pool-entitlements" required type="number" min={0} max={1000000} className="input h-11 tabular-nums"
            value={form.entitlements} onChange={(event) => set('entitlements', Number(event.target.value))} />
        </Field>
        <Field label="Reference" htmlFor="pool-reference" hint="The purchase order or agreement it was bought under.">
          <input id="pool-reference" maxLength={100} className="input h-11" value={form.reference ?? ''} onChange={(event) => set('reference', event.target.value || null)} />
        </Field>
        <Field label="Purchased" htmlFor="pool-purchased">
          <input id="pool-purchased" type="date" className="input h-11" value={form.purchaseDate ?? ''} onChange={(event) => set('purchaseDate', event.target.value || null)} />
        </Field>
        <Field label="Expires" htmlFor="pool-expires" hint="Blank means perpetual.">
          <input id="pool-expires" type="date" className="input h-11" value={form.expiresAt ?? ''} onChange={(event) => set('expiresAt', event.target.value || null)} />
          {invalidDates && <span className="mt-1 block text-xs text-red-600">A licence cannot expire before it was bought.</span>}
        </Field>
        <div className="sm:col-span-2">
          <Field label="Notes" htmlFor="pool-notes">
            <textarea id="pool-notes" maxLength={2000} rows={3} className="input h-auto py-2" value={form.notes ?? ''} onChange={(event) => set('notes', event.target.value || null)} />
          </Field>
        </div>
      </div>

      {error && <p role="alert" className="mt-4 text-sm text-red-600">{error}</p>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={pending || invalidDates || !complete}>{pending ? 'Saving…' : pool ? 'Save changes' : 'Create pool'}</Button>
      </div>
    </form>
  </div>
}

function Field({ label, htmlFor, hint, children }: { label: string; htmlFor: string; hint?: string; children: ReactNode }) {
  return <label className="block" htmlFor={htmlFor}>
    <span className="mb-1 block text-[13px] font-medium text-slate-600 dark:text-slate-300">{label}</span>
    {children}
    {hint && <span className="mt-1 block text-xs text-slate-500">{hint}</span>}
  </label>
}

function emptyPool(): LicensePoolInput {
  return { productId: '', name: '', reference: null, entitlements: 1, purchaseDate: null, expiresAt: null, notes: null }
}
