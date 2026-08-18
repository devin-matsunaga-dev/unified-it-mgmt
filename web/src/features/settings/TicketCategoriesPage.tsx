import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, CornerDownRight, Pencil, Plus, Tags, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import { helpdeskApi, type TicketCategory, type UpdateTicketCategoryInput } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { flatten, maximumDepth, parentOptions, type FlatCategory } from './categoryTree'

type Editing = { category: TicketCategory; path: string } | null

/**
 * Ticket categories, as the tree the pickers read. Deactivating hides a category from every picker
 * without touching the tickets already filed under it, which is why deleting one that is in use is
 * refused rather than cascaded.
 */
export function TicketCategoriesPage() {
  const queryClient = useQueryClient()
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editing, setEditing] = useState<Editing>(null)
  const [defaultParentId, setDefaultParentId] = useState<string | null>(null)
  const [showInactive, setShowInactive] = useState(true)

  const categories = useQuery({
    queryKey: ['ticket-categories', 'all'],
    queryFn: helpdeskApi.listCategoriesIncludingInactive,
    meta: { suppressErrorToast: true },
  })

  /** Every picker in the app reads the active-only key, so both have to be dropped after a write. */
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['ticket-categories'] })

  const save = useMutation({
    mutationFn: (input: UpdateTicketCategoryInput) => editing
      ? helpdeskApi.updateCategory(editing.category.id, input)
      : helpdeskApi.createCategory({ name: input.name, parentId: input.parentId, sortOrder: input.sortOrder }),
    onSuccess: async (category) => {
      await refresh()
      toast.success(`${category.name} ${editing ? 'updated' : 'created'}`)
      closeDialog()
    },
  })

  const remove = useMutation({
    mutationFn: (category: TicketCategory) => helpdeskApi.deleteCategory(category.id),
    onSuccess: async () => {
      await refresh()
      toast.success('Category deleted')
    },
    onError: (error: Error) => toast.error(error.message),
  })

  function closeDialog() {
    if (save.isPending) return
    setDialogOpen(false)
    setEditing(null)
    setDefaultParentId(null)
    save.reset()
  }

  function openCreate(parentId: string | null) {
    setEditing(null)
    setDefaultParentId(parentId)
    setDialogOpen(true)
  }

  const tree = categories.data ?? []
  const rows = flatten(tree).filter((row) => showInactive || row.category.isActive)
  const inactiveCount = flatten(tree).filter((row) => !row.category.isActive).length

  return <div className="space-y-6">
    <Link to="/admin/settings" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to settings</Link>

    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[28px] font-bold">Ticket categories</h1>
        <p className="mt-1 text-sm text-slate-500">What people choose when they raise a ticket. Nests up to {maximumDepth} levels deep.</p>
      </div>
      <div className="flex items-center gap-4 sm:ml-auto">
        {inactiveCount > 0 && <label className="flex items-center gap-2 text-[13px] font-medium text-slate-600 dark:text-slate-300">
          <input type="checkbox" className="size-4 rounded border-slate-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
            checked={showInactive} onChange={(event) => setShowInactive(event.target.checked)} />
          Show inactive ({inactiveCount})
        </label>}
        <Button onClick={() => openCreate(null)}><Plus size={18} />New category</Button>
      </div>
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      {categories.isLoading ? <div aria-label="Loading categories" className="space-y-px p-4">{Array.from({ length: 5 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
        : categories.isError ? <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
            <h2 className="font-semibold">Categories could not be loaded</h2>
            <p className="mt-1 text-sm text-slate-500">{categories.error instanceof ApiError ? categories.error.message : 'Try again in a moment.'}</p>
            <Button className="mt-4" variant="secondary" onClick={() => void categories.refetch()}>Try again</Button>
          </div></div>
        : rows.length === 0 ? <div className="grid min-h-64 place-items-center p-8 text-center"><div>
            <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><Tags /></span>
            <h2 className="mt-3 font-semibold">No categories yet</h2>
            <p className="mt-1 text-sm text-slate-500">Categories are how tickets get routed and reported on, so start with a broad one like “Hardware”.</p>
            <Button className="mt-4" onClick={() => openCreate(null)}>New category</Button>
          </div></div>
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[820px] text-left text-sm">
              <thead><tr>
                {['Name', 'Status', 'Custom fields', 'Sort order', ''].map((header) => <th key={header} className={`h-11 px-4 text-[13px] font-medium text-slate-500 ${header === 'Sort order' ? 'text-right' : ''}`}>{header}</th>)}
              </tr></thead>
              <tbody>
                {rows.map(({ category, depth, path }) => <tr key={category.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4 font-medium text-slate-900 dark:text-slate-100">
                    <span className="flex items-center gap-1.5" style={{ paddingLeft: depth * 20 }}>
                      {depth > 0 && <CornerDownRight size={15} aria-hidden className="shrink-0 text-slate-400" />}
                      {category.name}
                    </span>
                  </td>
                  <td className="h-12 px-4">
                    <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${category.isActive ? 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400' : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}`}>
                      {category.isActive ? 'Active' : 'Inactive'}
                    </span>
                  </td>
                  <td className="h-12 px-4 tabular-nums text-slate-600 dark:text-slate-300">{category.fields.length}</td>
                  <td className="h-12 px-4 text-right tabular-nums text-slate-600 dark:text-slate-300">{category.sortOrder}</td>
                  <td className="h-12 px-4 text-right whitespace-nowrap">
                    {depth + 1 < maximumDepth && <Button variant="ghost" className="h-8 px-2 text-[13px]" onClick={() => openCreate(category.id)} aria-label={`Add a category under ${category.name}`}><Plus size={15} />Add child</Button>}
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" onClick={() => { setEditing({ category, path }); setDefaultParentId(null); setDialogOpen(true) }} aria-label={`Edit ${category.name}`}><Pencil size={15} />Edit</Button>
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" disabled={remove.isPending} aria-label={`Delete ${category.name}`}
                      onClick={() => { if (window.confirm(`Delete ${path}? Categories that already have tickets or child categories cannot be deleted — deactivate it instead.`)) remove.mutate(category) }}>
                      <Trash2 size={15} />Delete
                    </Button>
                  </td>
                </tr>)}
              </tbody>
            </table>
          </div>}
    </section>

    <CategoryFormDialog open={dialogOpen} editing={editing} tree={tree} defaultParentId={defaultParentId}
      pending={save.isPending} error={save.error instanceof Error ? save.error.message : undefined}
      onClose={closeDialog} onSubmit={(input) => save.mutate(input)} />
  </div>
}

function CategoryFormDialog({ open, editing, tree, defaultParentId, pending, error, onClose, onSubmit }: {
  open: boolean
  editing: Editing
  tree: TicketCategory[]
  defaultParentId: string | null
  pending: boolean
  error?: string
  onClose: () => void
  /**
   * Fire-and-forget on purpose: the mutation's own error state renders the alert below, so awaiting
   * a rejected mutateAsync here would only raise an unhandled rejection saying the same thing.
   */
  onSubmit: (input: UpdateTicketCategoryInput) => void
}) {
  const [form, setForm] = useState<UpdateTicketCategoryInput>(emptyCategory(null))

  useEffect(() => {
    if (!open) return
    setForm(editing
      ? { name: editing.category.name, parentId: editing.category.parentId, isActive: editing.category.isActive, sortOrder: editing.category.sortOrder }
      : emptyCategory(defaultParentId))
  }, [open, editing, defaultParentId])

  if (!open) return null

  const options: FlatCategory[] = parentOptions(tree, editing?.category ?? null)
  const set = <K extends keyof UpdateTicketCategoryInput>(key: K, value: UpdateTicketCategoryInput[K]) =>
    setForm((current) => ({ ...current, [key]: value }))

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={editing ? `Edit ${editing.path}` : 'New category'}>
    <form className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); onSubmit({ ...form, name: form.name.trim() }) }}>
      <h2 className="text-lg font-semibold">{editing ? 'Edit category' : 'New category'}</h2>

      <div className="mt-5 space-y-4">
        <Field label="Name" htmlFor="category-name">
          <input id="category-name" required maxLength={100} autoFocus className="input h-11"
            value={form.name} onChange={(event) => set('name', event.target.value)} />
        </Field>

        <Field label="Parent category" htmlFor="category-parent"
          hint={`Leave as “None” for a top-level category. Nesting stops at ${maximumDepth} levels.`}>
          <select id="category-parent" aria-describedby="category-parent-hint" className="input h-11" value={form.parentId ?? ''}
            onChange={(event) => set('parentId', event.target.value || null)}>
            <option value="">None — top level</option>
            {options.map(({ category, path }) => <option key={category.id} value={category.id}>{path}</option>)}
          </select>
        </Field>

        <Field label="Sort order" htmlFor="category-sort" hint="Lower numbers come first; ties fall back to the name.">
          <input id="category-sort" aria-describedby="category-sort-hint" type="number" min={0} max={10000} className="input h-11"
            value={form.sortOrder} onChange={(event) => set('sortOrder', Number(event.target.value) || 0)} />
        </Field>

        {editing && <div>
          <label className="flex items-center gap-2 text-[13px] font-medium text-slate-600 dark:text-slate-300">
            <input type="checkbox" className="size-4 rounded border-slate-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
              checked={form.isActive} onChange={(event) => set('isActive', event.target.checked)} />
            Active
          </label>
          <p className="mt-1.5 text-[13px] text-slate-500">An inactive category disappears from every picker. Tickets already filed under it keep it.</p>
        </div>}
      </div>

      {error && <p role="alert" className="mt-4 text-xs text-red-600">{error}</p>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={pending || !form.name.trim()}>{pending ? 'Saving…' : editing ? 'Save category' : 'Create category'}</Button>
      </div>
    </form>
  </div>
}

function emptyCategory(parentId: string | null): UpdateTicketCategoryInput {
  return { name: '', parentId, isActive: true, sortOrder: 0 }
}

/**
 * The hint sits outside the <label> so it does not become part of the field's accessible name — the
 * defect WP-5.7 and WP-5.9 both hit. Callers point their input at it with aria-describedby.
 */
function Field({ label, htmlFor, hint, children }: {
  label: string
  htmlFor: string
  hint?: string
  children: React.ReactNode
}) {
  return <div>
    <label htmlFor={htmlFor} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">{label}</label>
    {hint && <p id={`${htmlFor}-hint`} className="mb-1.5 text-[13px] text-slate-500">{hint}</p>}
    {children}
  </div>
}
