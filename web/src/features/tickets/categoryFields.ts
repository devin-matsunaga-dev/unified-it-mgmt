import type { TicketCategory, TicketCustomField } from '../../api/helpdesk'

export type CategoryOption = { id: string; name: string; depth: number }

/** Depth-first list of the category tree, so a single select can render the whole hierarchy. */
export function flattenCategories(categories: TicketCategory[], depth = 0): CategoryOption[] {
  return categories.flatMap((category) => [{ id: category.id, name: category.name, depth }, ...flattenCategories(category.children, depth + 1)])
}

export function findCategory(categories: TicketCategory[], id: string | null): TicketCategory | null {
  if (!id) return null
  for (const category of categories) {
    if (category.id === id) return category
    const match = findCategory(category.children, id)
    if (match) return match
  }
  return null
}

/** Mirrors the server-side custom-field rules so the form blocks an invalid submit before it is sent. */
export function validateCustomFields(fields: TicketCustomField[], values: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {}
  for (const field of fields) {
    const value = (values[field.key] ?? '').trim()
    if (!value) {
      if (field.isRequired) errors[field.key] = `${field.label} is required.`
      continue
    }
    if (field.type === 'Number' && !/^-?\d+(\.\d+)?$/.test(value)) errors[field.key] = `${field.label} must be a number.`
    else if (field.type === 'Date' && !/^\d{4}-\d{2}-\d{2}$/.test(value)) errors[field.key] = `${field.label} must be a date in yyyy-MM-dd format.`
    else if (field.type === 'Select' && !field.options.includes(value)) errors[field.key] = `${field.label} must be one of: ${field.options.join(', ')}.`
    else if (field.type === 'Text' && value.length > 1000) errors[field.key] = `${field.label} must be 1000 characters or fewer.`
  }
  return errors
}

/** Drops blanks and values left behind by a previously selected category. */
export function customFieldPayload(fields: TicketCustomField[], values: Record<string, string>): Record<string, string> {
  return Object.fromEntries(fields.map((field) => [field.key, (values[field.key] ?? '').trim()]).filter(([, value]) => value !== ''))
}
