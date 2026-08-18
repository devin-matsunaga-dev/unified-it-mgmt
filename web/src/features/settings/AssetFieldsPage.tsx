import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, ListPlus, Lock, Pencil, Plus, Trash2, X } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import {
  assetsApi, ciTypeLabel,
  type CiCustomField, type CiCustomFieldType, type CiCustomFieldValueCount, type CiType,
} from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { fieldKeyMaxLength, toFieldKey } from './fieldKey'

const ciTypes: CiType[] = ['Hardware', 'Server', 'NetworkDevice', 'Software', 'Virtual', 'Logical']
const fieldTypes: CiCustomFieldType[] = ['Text', 'Number', 'Date', 'Select']

/**
 * The fields each CI type carries beyond its fixed columns.
 *
 * This is where a subtype comes from: <c>CiType</c> stops at "Hardware", so a Select field with the
 * options an organisation actually uses — Laptop, Desktop, Printer — is how a Hardware CI says which
 * one it is. Those fields become the sub-filters on the asset list.
 */
export function AssetFieldsPage() {
  const queryClient = useQueryClient()
  const [ciType, setCiType] = useState<CiType>('Hardware')
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editing, setEditing] = useState<CiCustomField | null>(null)

  const schemas = useQuery({
    queryKey: ['ci-type-schemas'],
    queryFn: assetsApi.listTypeSchemas,
    meta: { suppressErrorToast: true },
  })

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['ci-type-schemas'] })

  const create = useMutation({
    // Wrapped rather than passed bare: TanStack calls a mutationFn with a second context argument,
    // and handing an API function extra arguments is how the listCategories trap started.
    mutationFn: (input: Parameters<typeof assetsApi.createCustomField>[0]) =>
      assetsApi.createCustomField(input),
    onSuccess: async (field) => {
      await refresh()
      toast.success(`${field.label} added to ${ciTypeLabel(ciType)}`)
      setDialogOpen(false)
      create.reset()
    },
  })

  const update = useMutation({
    mutationFn: (input: { id: string; label: string; isRequired: boolean; options: string[]; sortOrder: number }) =>
      assetsApi.updateCustomField(input.id, input),
    onSuccess: async (field) => {
      await refresh()
      toast.success(`${field.label} updated`)
      setDialogOpen(false)
      setEditing(null)
      update.reset()
    },
  })

  const remove = useMutation({
    mutationFn: (field: CiCustomField) => assetsApi.deleteCustomField(field.id),
    onSuccess: async () => { await refresh(); toast.success('Field deleted') },
    onError: (error: Error) => toast.error(error.message),
  })

  const schema = schemas.data?.find((item) => item.type === ciType) ?? null
  const fields = schema?.customFields ?? []

  return <div className="space-y-6">
    <Link to="/admin/settings" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to settings</Link>

    <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
      <div>
        <h1 className="text-[28px] font-bold">Asset fields</h1>
        <p className="mt-1 text-sm text-slate-500">
          Extra fields each kind of CI carries. A “choose one” field becomes a filter on the asset list —
          which is how hardware is split into laptops, desktops and printers.
        </p>
      </div>
      <Button className="sm:ml-auto" onClick={() => { create.reset(); setEditing(null); setDialogOpen(true) }}>
        <Plus size={18} />New field
      </Button>
    </div>

    <div className="flex flex-wrap gap-1 border-b border-slate-200 dark:border-slate-800" role="tablist">
      {ciTypes.map((type) => <button key={type} type="button" role="tab"
        aria-selected={ciType === type}
        onClick={() => setCiType(type)}
        className={`-mb-px border-b-2 px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 ${ciType === type ? 'border-blue-600 text-blue-600' : 'border-transparent text-slate-500 hover:text-slate-900 dark:hover:text-slate-100'}`}>
        {ciTypeLabel(type)}
      </button>)}
    </div>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      {schemas.isLoading ? <div aria-label="Loading fields" className="space-y-px p-4">{Array.from({ length: 3 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
        : schemas.isError ? <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
            <h2 className="font-semibold">Fields could not be loaded</h2>
            <p className="mt-1 text-sm text-slate-500">{schemas.error instanceof ApiError ? schemas.error.message : 'Try again in a moment.'}</p>
            <Button className="mt-4" variant="secondary" onClick={() => void schemas.refetch()}>Try again</Button>
          </div></div>
        : fields.length === 0 ? <div className="grid min-h-64 place-items-center p-8 text-center"><div>
            <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><ListPlus /></span>
            <h2 className="mt-3 font-semibold">No extra fields on {ciTypeLabel(ciType)}</h2>
            <p className="mt-1 text-sm text-slate-500">Add a “choose one” field to split this type into the kinds you actually track.</p>
            <Button className="mt-4" onClick={() => { create.reset(); setDialogOpen(true) }}>New field</Button>
          </div></div>
        : <div className="overflow-x-auto">
            <table className="w-full min-w-[760px] text-left text-sm">
              <thead><tr>
                {['Label', 'Key', 'Kind', 'Options', 'Required', ''].map((header) => <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}
              </tr></thead>
              <tbody>
                {fields.map((field) => <tr key={field.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                  <td className="h-12 px-4 font-medium text-slate-900 dark:text-slate-100">{field.label}</td>
                  <td className="h-12 px-4 font-mono text-xs text-slate-500">{field.key}</td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{field.type === 'Select' ? 'Choose one' : field.type}</td>
                  <td className="h-12 px-4">
                    {field.options.length === 0
                      ? <span className="text-[13px] text-slate-400">—</span>
                      : <span className="flex flex-wrap gap-1">{field.options.map((option) =>
                          <span key={option} className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">{option}</span>)}
                        </span>}
                  </td>
                  <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{field.isRequired ? 'Yes' : 'No'}</td>
                  <td className="h-12 px-4 text-right whitespace-nowrap">
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" aria-label={`Edit ${field.label}`}
                      onClick={() => { update.reset(); setEditing(field); setDialogOpen(true) }}>
                      <Pencil size={15} />Edit
                    </Button>
                    <Button variant="ghost" className="h-8 px-2 text-[13px]" aria-label={`Delete ${field.label}`} disabled={remove.isPending}
                      onClick={() => { if (window.confirm(`Delete ${field.label}? A field that already holds values on assets cannot be deleted.`)) remove.mutate(field) }}>
                      <Trash2 size={15} />Delete
                    </Button>
                  </td>
                </tr>)}
              </tbody>
            </table>
          </div>}
    </section>

    {dialogOpen && <FieldDialog
      key={editing?.id ?? 'new'}
      ciType={ciType}
      editing={editing}
      pending={create.isPending || update.isPending}
      error={(editing ? update.error : create.error) instanceof Error
        ? (editing ? update.error : create.error)!.message
        : undefined}
      onClose={() => {
        if (create.isPending || update.isPending) return
        setDialogOpen(false)
        setEditing(null)
        create.reset()
        update.reset()
      }}
      onSubmit={(input) => editing
        ? update.mutate({ id: editing.id, ...input, sortOrder: editing.sortOrder })
        : create.mutate({ ciType, ...input })} />}
  </div>
}

type FieldDraft = {
  key: string
  label: string
  type: CiCustomFieldType
  isRequired: boolean
  options: string[]
}

function FieldDialog({ ciType, editing, pending, error, onClose, onSubmit }: {
  ciType: CiType
  /** Null when creating. Editing locks the key and the kind — see the note beside them. */
  editing: CiCustomField | null
  pending: boolean
  error?: string
  onClose: () => void
  /** Fire-and-forget: the mutation's error state renders the alert below. */
  onSubmit: (input: FieldDraft) => void
}) {
  const [form, setForm] = useState<FieldDraft>(editing
    ? { key: editing.key, label: editing.label, type: editing.type, isRequired: editing.isRequired, options: [...editing.options] }
    : { key: '', label: '', type: 'Select', isRequired: false, options: [] })
  const [option, setOption] = useState('')

  /**
   * Whether the key still follows the label. Typing in the key stops it, and it never starts again —
   * a key someone has chosen must not be overwritten by a later edit to the label. Editing an
   * existing field never generates, because its key is fixed.
   */
  const [keyFollowsLabel, setKeyFollowsLabel] = useState(editing === null)

  /**
   * How many CIs hold each option, read only while the dialog is open. Adding an option is always
   * safe; removing one the estate still uses is refused by the server, so the count is shown here to
   * make that visible before anybody tries.
   */
  const counts = useQuery({
    queryKey: ['ci-custom-fields', editing?.id, 'value-counts'],
    queryFn: () => assetsApi.getCustomFieldValueCounts(editing!.id),
    enabled: editing !== null,
    meta: { suppressErrorToast: true },
  })

  const usedBy = (value: string) =>
    counts.data?.find((count: CiCustomFieldValueCount) => count.value === value)?.ciCount ?? 0

  const addOption = () => {
    const value = option.trim()
    if (!value || form.options.includes(value)) return
    setForm((current) => ({ ...current, options: [...current.options, value] }))
    setOption('')
  }

  const needsOptions = form.type === 'Select'
  const complete = form.key.trim() !== '' && form.label.trim() !== '' && (!needsOptions || form.options.length > 0)

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={editing ? `Edit ${editing.label}` : `New field on ${ciTypeLabel(ciType)}`}>
    <form className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => {
        event.preventDefault()
        onSubmit({ ...form, key: form.key.trim(), label: form.label.trim() })
      }}>
      <h2 className="text-lg font-semibold">
        {editing ? `Edit ${editing.label}` : `New field on ${ciTypeLabel(ciType)}`}
      </h2>

      <div className="mt-5 grid gap-4 sm:grid-cols-2">
        <div>
          <label htmlFor="field-label" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Label</label>
          <input id="field-label" required maxLength={100} autoFocus className="input h-11"
            value={form.label}
            onChange={(event) => {
              const label = event.target.value
              setForm((current) => ({
                ...current,
                label,
                key: keyFollowsLabel ? toFieldKey(label) : current.key,
              }))
            }} />
        </div>
        <div>
          <label htmlFor="field-key" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Key</label>
          <p id="field-key-hint" className="mb-1.5 text-[13px] text-slate-500">
            {editing
              ? 'Fixed. Every stored value and every import refers to the field by this name.'
              : keyFollowsLabel
                ? 'Generated from the label. Type here to set it yourself — it cannot be changed later.'
                : 'Letters, digits and underscores. Cannot be changed later.'}
          </p>
          <input id="field-key" aria-describedby="field-key-hint" required maxLength={fieldKeyMaxLength}
            readOnly={editing !== null}
            className={`input h-11 font-mono${editing ? ' cursor-not-allowed bg-slate-50 text-slate-500 dark:bg-slate-800' : ''}`}
            value={form.key}
            onChange={(event) => {
              setKeyFollowsLabel(false)
              setForm((current) => ({ ...current, key: event.target.value }))
            }} />
        </div>
      </div>

      <div className="mt-4">
        <label htmlFor="field-type" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Kind</label>
        <select id="field-type" className="input h-11" value={form.type} disabled={editing !== null}
          onChange={(event) => setForm((current) => ({ ...current, type: event.target.value as CiCustomFieldType }))}>
          {fieldTypes.map((type) => <option key={type} value={type}>{type === 'Select' ? 'Choose one' : type}</option>)}
        </select>
        {editing && <p className="mt-1.5 flex items-center gap-1.5 text-[13px] text-slate-500">
          <Lock size={12} />The kind decides how stored values are read, so it cannot change once assets hold them.
        </p>}
      </div>

      {needsOptions && <fieldset className="mt-4">
        <legend className="text-[13px] font-medium text-slate-600 dark:text-slate-300">Options</legend>
        <p className="mt-1 text-[13px] text-slate-500">Each option becomes a choice on the asset form and a filter on the list.</p>
        <div className="mt-2 flex gap-2">
          <input aria-label="New option" maxLength={100} className="input h-10 flex-1" value={option}
            onChange={(event) => setOption(event.target.value)}
            onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); addOption() } }} />
          <Button type="button" variant="secondary" onClick={addOption} disabled={option.trim() === ''}>Add</Button>
        </div>
        {form.options.length > 0 && <ul className="mt-2 flex flex-wrap gap-1">
          {form.options.map((value) => {
            const inUse = usedBy(value)
            return <li key={value}>
              <button type="button"
                aria-label={inUse > 0 ? `${value}, used by ${inUse} assets` : `Remove ${value}`}
                disabled={inUse > 0}
                title={inUse > 0 ? `${inUse} assets are recorded as ${value}. Change those first.` : undefined}
                onClick={() => setForm((current) => ({ ...current, options: current.options.filter((item) => item !== value) }))}
                className={`flex items-center gap-1 rounded-md px-2 py-1 text-xs font-medium ${inUse > 0
                  ? 'cursor-not-allowed bg-slate-50 text-slate-400 dark:bg-slate-800/60 dark:text-slate-500'
                  : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-300'}`}>
                {value}
                {inUse > 0 ? <span className="tabular-nums">· {inUse}</span> : <X size={12} />}
              </button>
            </li>
          })}
        </ul>}
        {editing && <p className="mt-2 text-[13px] text-slate-500">
          Adding an option is always safe. One an asset is already recorded as cannot be removed until
          those assets are changed.
        </p>}
      </fieldset>}

      <label className="mt-4 flex items-center gap-2 text-[13px] font-medium text-slate-600 dark:text-slate-300">
        <input type="checkbox" className="size-4 rounded border-slate-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
          checked={form.isRequired} onChange={(event) => setForm((current) => ({ ...current, isRequired: event.target.checked }))} />
        Required on every {ciTypeLabel(ciType)}
      </label>
      {form.isRequired && <p className="mt-1.5 text-[13px] text-amber-700 dark:text-amber-400">
        Existing assets have no value for this field, so the next edit of each will ask for one.
      </p>}

      {error && <p role="alert" className="mt-4 text-xs text-red-600">{error}</p>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={pending || !complete}>
          {pending ? 'Saving…' : editing ? 'Save field' : 'Create field'}
        </Button>
      </div>
    </form>
  </div>
}
