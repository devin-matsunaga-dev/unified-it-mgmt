import type { TicketCategory, TicketCustomField } from '../../api/helpdesk'
import { flattenCategories } from './categoryFields'

export function CategorySelect({ categories, value, onChange, error, id = 'ticket-category', placeholder = 'Select a category' }: { categories: TicketCategory[]; value: string; onChange: (categoryId: string) => void; error?: string; id?: string; placeholder?: string }) {
  return <>
    <select id={id} className="input h-11" value={value} onChange={(event) => onChange(event.target.value)}>
      <option value="">{placeholder}</option>
      {flattenCategories(categories).map((option) => <option key={option.id} value={option.id}>{'  '.repeat(option.depth)}{option.depth > 0 ? '↳ ' : ''}{option.name}</option>)}
    </select>
    {error && <span className="mt-1.5 block text-xs text-red-600">{error}</span>}
  </>
}

export function CustomFieldInputs({ fields, values, errors, onChange, idPrefix = 'custom-field' }: { fields: TicketCustomField[]; values: Record<string, string>; errors: Record<string, string>; onChange: (key: string, value: string) => void; idPrefix?: string }) {
  if (fields.length === 0) return null
  return <>
    {fields.map((field) => {
      const inputId = `${idPrefix}-${field.key}`
      const value = values[field.key] ?? ''
      return <div key={field.id}>
        <label htmlFor={inputId} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">
          {field.label}{field.isRequired && <span aria-hidden="true" className="ml-1 text-red-600">*</span>}
          {field.isRequired && <span className="sr-only"> (required)</span>}
        </label>
        {field.type === 'Select'
          ? <select id={inputId} className="input h-11" value={value} onChange={(event) => onChange(field.key, event.target.value)} aria-invalid={Boolean(errors[field.key])}>
              <option value="">Select an option</option>
              {field.options.map((option) => <option key={option} value={option}>{option}</option>)}
            </select>
          : <input id={inputId} className="input h-11" type={inputType(field)} inputMode={field.type === 'Number' ? 'decimal' : undefined} value={value} onChange={(event) => onChange(field.key, event.target.value)} aria-invalid={Boolean(errors[field.key])} />}
        {errors[field.key] && <span role="alert" className="mt-1.5 block text-xs text-red-600">{errors[field.key]}</span>}
      </div>
    })}
  </>
}

function inputType(field: TicketCustomField) {
  return field.type === 'Date' ? 'date' : field.type === 'Number' ? 'number' : 'text'
}
