import { useEffect, useMemo, useState } from 'react'
import type { Ci, CiAttributeDefinition, CiCustomField, CiType, CiTypeSchema } from '../../api/assets'
import { ciTypeLabel, ciTypes } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { ciValuePayload, schemaFor, validateAttributes, validateCiCustomFields } from './ciFields'

export type CiFormSubmit = {
  type: CiType
  name: string
  assetTag: string | null
  serialNumber: string | null
  description: string | null
  isActive: boolean
  attributes: Record<string, string>
  customFields: Record<string, string>
}

/**
 * Values to open a *new* CI form with. Separate from `ci` on purpose: passing a CI switches the
 * dialog to editing — the type locks, the title changes, the active checkbox appears — and a device
 * that has been identified but not yet created is none of those things.
 */
export type CiFormSeed = {
  type?: CiType
  name?: string
  assetTag?: string
  serialNumber?: string
  attributes?: Record<string, string>
}

export function CiFormDialog({ open, ci, seed, schemas, pending, error, onClose, onSubmit }: {
  open: boolean
  ci: Ci | null
  /** Prefill for a new CI. Ignored when `ci` is set, which is an edit. */
  seed?: CiFormSeed | null
  schemas: CiTypeSchema[]
  pending: boolean
  error?: string
  onClose: () => void
  onSubmit: (input: CiFormSubmit) => Promise<void>
}) {
  const [type, setType] = useState<CiType>('Hardware')
  const [name, setName] = useState('')
  const [assetTag, setAssetTag] = useState('')
  const [serialNumber, setSerialNumber] = useState('')
  const [description, setDescription] = useState('')
  const [isActive, setIsActive] = useState(true)
  const [attributes, setAttributes] = useState<Record<string, string>>({})
  const [customFields, setCustomFields] = useState<Record<string, string>>({})
  const [errors, setErrors] = useState<Record<string, string>>({})

  // Reopening the dialog reloads it from the CI being edited, so a previous edit never leaks in.
  useEffect(() => {
    if (!open) return
    setType(ci?.type ?? seed?.type ?? 'Hardware')
    setName(ci?.name ?? seed?.name ?? '')
    setAssetTag(ci?.assetTag ?? seed?.assetTag ?? '')
    setSerialNumber(ci?.serialNumber ?? seed?.serialNumber ?? '')
    setDescription(ci?.description ?? '')
    setIsActive(ci?.isActive ?? true)
    setAttributes(ci?.attributes ?? seed?.attributes ?? {})
    setCustomFields(Object.fromEntries((ci?.customFields ?? []).map((value) => [value.key, value.value])))
    setErrors({})
  }, [open, ci, seed])

  const schema = useMemo(() => schemaFor(schemas, type), [schemas, type])
  const definitions = schema?.attributes ?? []
  const fields = schema?.customFields ?? []

  if (!open) return null

  const submit = async () => {
    const nextErrors: Record<string, string> = {}
    if (!name.trim()) nextErrors.name = 'Name is required.'
    const attributeErrors = validateAttributes(definitions, attributes)
    const fieldErrors = validateCiCustomFields(fields, customFields)
    for (const [key, message] of Object.entries(attributeErrors)) nextErrors[`attributes.${key}`] = message
    for (const [key, message] of Object.entries(fieldErrors)) nextErrors[`customFields.${key}`] = message
    setErrors(nextErrors)
    if (Object.keys(nextErrors).length > 0) return

    await onSubmit({
      type,
      name: name.trim(),
      assetTag: assetTag.trim() || null,
      serialNumber: serialNumber.trim() || null,
      description: description.trim() || null,
      isActive,
      attributes: ciValuePayload(definitions.map((definition) => definition.key), attributes),
      customFields: ciValuePayload(fields.map((field) => field.key), customFields),
    })
  }

  return <div className="fixed inset-0 z-20 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={ci ? 'Edit CI' : 'New CI'}>
    <div className="max-h-full w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-lg font-semibold">{ci ? `Edit ${ci.name}` : 'New configuration item'}</h2>
      <p className="mt-1 text-sm text-slate-500">{ci ? 'The CI type cannot be changed after creation.' : 'Pick a type — its required attributes appear below.'}</p>
      {error && <p role="alert" className="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700 dark:border-red-900 dark:bg-red-950">{error}</p>}
      <div className="mt-5 grid gap-4 sm:grid-cols-2">
        <Field label="Type" htmlFor="ci-type">
          <select id="ci-type" className="input h-11" value={type} disabled={Boolean(ci)} onChange={(event) => { setType(event.target.value as CiType); setAttributes({}); setCustomFields({}); setErrors({}) }}>
            {ciTypes.map((option) => <option key={option} value={option}>{ciTypeLabel(option)}</option>)}
          </select>
        </Field>
        <Field label="Name" htmlFor="ci-name" required error={errors.name}>
          <input id="ci-name" className="input h-11" value={name} onChange={(event) => setName(event.target.value)} aria-invalid={Boolean(errors.name)} />
        </Field>
        <Field label="Asset tag" htmlFor="ci-asset-tag">
          <input id="ci-asset-tag" className="input h-11" value={assetTag} onChange={(event) => setAssetTag(event.target.value)} />
        </Field>
        <Field label="Serial number" htmlFor="ci-serial">
          <input id="ci-serial" className="input h-11" value={serialNumber} onChange={(event) => setSerialNumber(event.target.value)} />
        </Field>
        <div className="sm:col-span-2">
          <Field label="Description" htmlFor="ci-description">
            <textarea id="ci-description" rows={2} className="input py-2" value={description} onChange={(event) => setDescription(event.target.value)} />
          </Field>
        </div>
      </div>

      <h3 className="mt-6 text-[13px] font-medium text-slate-500">{ciTypeLabel(type)} attributes</h3>
      <div className="mt-2 grid gap-4 sm:grid-cols-2">
        {definitions.map((definition) => <AttributeInput key={definition.key} definition={definition} value={attributes[definition.key] ?? ''} error={errors[`attributes.${definition.key}`]} onChange={(value) => setAttributes((current) => ({ ...current, [definition.key]: value }))} />)}
      </div>

      {fields.length > 0 && <>
        <h3 className="mt-6 text-[13px] font-medium text-slate-500">Custom fields</h3>
        <div className="mt-2 grid gap-4 sm:grid-cols-2">
          {fields.map((field) => <CustomFieldInput key={field.id} field={field} value={customFields[field.key] ?? ''} error={errors[`customFields.${field.key}`]} onChange={(value) => setCustomFields((current) => ({ ...current, [field.key]: value }))} />)}
        </div>
      </>}

      {ci && <label className="mt-6 flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300"><input type="checkbox" checked={isActive} onChange={(event) => setIsActive(event.target.checked)} />Active</label>}

      <div className="mt-6 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose} disabled={pending}>Cancel</Button>
        <Button onClick={() => void submit()} disabled={pending}>{pending ? 'Saving…' : ci ? 'Save changes' : 'Create CI'}</Button>
      </div>
    </div>
  </div>
}

function AttributeInput({ definition, value, error, onChange }: { definition: CiAttributeDefinition; value: string; error?: string; onChange: (value: string) => void }) {
  const id = `ci-attribute-${definition.key}`
  return <Field label={definition.label} htmlFor={id} required={definition.isRequired} error={error}>
    {definition.kind === 'Choice'
      ? <select id={id} className="input h-11" value={value} onChange={(event) => onChange(event.target.value)} aria-invalid={Boolean(error)}>
          {/* Blank stays selectable: an optional Choice has to be clearable back to "not set". */}
          <option value="">{definition.isRequired ? 'Choose…' : 'Not set'}</option>
          {definition.allowedValues.map((allowed) => <option key={allowed} value={allowed}>{allowed}</option>)}
        </select>
      : <input id={id} className="input h-11" type={definition.kind === 'Integer' ? 'number' : 'text'} inputMode={definition.kind === 'Integer' ? 'numeric' : undefined} min={definition.kind === 'Integer' ? 0 : undefined} value={value} onChange={(event) => onChange(event.target.value)} aria-invalid={Boolean(error)} />}
  </Field>
}

function CustomFieldInput({ field, value, error, onChange }: { field: CiCustomField; value: string; error?: string; onChange: (value: string) => void }) {
  const id = `ci-custom-field-${field.key}`
  return <Field label={field.label} htmlFor={id} required={field.isRequired} error={error}>
    {field.type === 'Select'
      ? <select id={id} className="input h-11" value={value} onChange={(event) => onChange(event.target.value)} aria-invalid={Boolean(error)}>
          <option value="">Select an option</option>
          {field.options.map((option) => <option key={option} value={option}>{option}</option>)}
        </select>
      : <input id={id} className="input h-11" type={field.type === 'Date' ? 'date' : field.type === 'Number' ? 'number' : 'text'} inputMode={field.type === 'Number' ? 'decimal' : undefined} value={value} onChange={(event) => onChange(event.target.value)} aria-invalid={Boolean(error)} />}
  </Field>
}

function Field({ label, htmlFor, required, error, children }: { label: string; htmlFor: string; required?: boolean; error?: string; children: React.ReactNode }) {
  return <div>
    <label htmlFor={htmlFor} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">
      {label}{required && <span aria-hidden="true" className="ml-1 text-red-600">*</span>}
      {required && <span className="sr-only"> (required)</span>}
    </label>
    {children}
    {error && <span role="alert" className="mt-1.5 block text-xs text-red-600">{error}</span>}
  </div>
}
