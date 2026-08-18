import type { CiAttributeDefinition, CiCustomField, CiTypeSchema, CiType } from '../../api/assets'

export function schemaFor(schemas: CiTypeSchema[], type: CiType): CiTypeSchema | null {
  return schemas.find((schema) => schema.type === type) ?? null
}

/** Mirrors CiTypeSchema.Bind so the form blocks an invalid submit before it is sent. */
export function validateAttributes(definitions: CiAttributeDefinition[], values: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {}
  for (const definition of definitions) {
    const value = (values[definition.key] ?? '').trim()
    if (!value) {
      if (definition.isRequired) errors[definition.key] = `${definition.label} is required.`
      continue
    }
    if (definition.kind === 'Integer' && !/^\d+$/.test(value)) errors[definition.key] = `${definition.label} must be a whole number of zero or more.`
    else if (definition.kind === 'IpAddress' && !isIpAddress(value)) errors[definition.key] = `${definition.label} must be a valid IPv4 or IPv6 address.`
    else if (definition.kind === 'Text' && value.length > 500) errors[definition.key] = `${definition.label} must be 500 characters or fewer.`
    else if (definition.kind === 'Choice' && !definition.allowedValues.some((allowed) => allowed.toLowerCase() === value.toLowerCase())) errors[definition.key] = `${definition.label} must be one of: ${definition.allowedValues.join(', ')}.`
  }
  return errors
}

/** Mirrors CiCustomFieldValueBinder.Bind. */
export function validateCiCustomFields(fields: CiCustomField[], values: Record<string, string>): Record<string, string> {
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

/** Drops blanks and values left behind by a previously selected CI type. */
export function ciValuePayload(keys: string[], values: Record<string, string>): Record<string, string> {
  return Object.fromEntries(keys.map((key) => [key, (values[key] ?? '').trim()]).filter(([, value]) => value !== ''))
}

/**
 * IPv4 is checked octet by octet because the browser has no parser and a loose regex would let
 * "10.0.0.999" through to a 400 the form cannot attribute to the field.
 */
function isIpAddress(value: string) {
  if (value.includes(':')) return /^[0-9a-fA-F:]+$/.test(value) && (value.match(/::/g) ?? []).length <= 1 && value.split(':').length <= 8
  const octets = value.split('.')
  return octets.length === 4 && octets.every((octet) => /^\d{1,3}$/.test(octet) && Number(octet) <= 255)
}
