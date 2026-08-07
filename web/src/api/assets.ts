import { apiRequest } from './client'

export type CiType = 'Hardware' | 'Server' | 'NetworkDevice' | 'Software' | 'Virtual' | 'Logical'
export type CiAttributeKind = 'Text' | 'Integer' | 'IpAddress'
export type CiCustomFieldType = 'Text' | 'Number' | 'Date' | 'Select'

/** A fixed attribute of a CI type — a real column, not a runtime field. */
export type CiAttributeDefinition = {
  key: string
  label: string
  kind: CiAttributeKind
  isRequired: boolean
}

export type CiCustomField = {
  id: string
  ciType: CiType
  key: string
  label: string
  type: CiCustomFieldType
  isRequired: boolean
  options: string[]
  sortOrder: number
}

/** Both halves of a type's form: fixed attributes plus whatever an admin has added at runtime. */
export type CiTypeSchema = {
  type: CiType
  attributes: CiAttributeDefinition[]
  customFields: CiCustomField[]
}

export type CiCustomFieldValue = {
  fieldId: string
  key: string
  label: string
  type: CiCustomFieldType
  value: string
}

export type Ci = {
  id: string
  type: CiType
  name: string
  assetTag: string | null
  serialNumber: string | null
  description: string | null
  isActive: boolean
  attributes: Record<string, string>
  customFields: CiCustomFieldValue[]
  createdAt: string
  updatedAt: string
}

export type CiPage = { items: Ci[]; total: number; page: number; pageSize: number }

export type CiFilter = {
  type?: CiType
  search?: string
  isActive?: boolean
  page?: number
  pageSize?: number
}

export type CreateCiInput = {
  type: CiType
  name: string
  assetTag: string | null
  serialNumber: string | null
  description: string | null
  attributes: Record<string, string>
  customFields: Record<string, string>
}

export type UpdateCiInput = Omit<CreateCiInput, 'type'> & { isActive: boolean }

export const ciTypes: CiType[] = ['Hardware', 'Server', 'NetworkDevice', 'Software', 'Virtual', 'Logical']

const ciTypeLabels: Record<CiType, string> = {
  Hardware: 'Hardware',
  Server: 'Server',
  NetworkDevice: 'Network device',
  Software: 'Software',
  Virtual: 'Virtual machine',
  Logical: 'Logical',
}

export function ciTypeLabel(type: CiType) {
  return ciTypeLabels[type] ?? type
}

export function ciFilterToQuery(filter: CiFilter) {
  const query = new URLSearchParams()
  if (filter.type) query.set('type', filter.type)
  if (filter.search?.trim()) query.set('search', filter.search.trim())
  if (filter.isActive !== undefined) query.set('isActive', String(filter.isActive))
  query.set('page', String(filter.page ?? 1))
  query.set('pageSize', String(filter.pageSize ?? 25))
  return query.toString()
}

export const assetsApi = {
  listCis: (filter: CiFilter = {}) => apiRequest<CiPage>(`/api/cis?${ciFilterToQuery(filter)}`),
  getCi: (id: string) => apiRequest<Ci>(`/api/cis/${id}`),
  createCi: (input: CreateCiInput) => apiRequest<Ci>('/api/cis', { method: 'POST', body: JSON.stringify(input) }),
  updateCi: (id: string, input: UpdateCiInput) => apiRequest<Ci>(`/api/cis/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteCi: (id: string) => apiRequest<void>(`/api/cis/${id}`, { method: 'DELETE' }),
  listTypeSchemas: () => apiRequest<CiTypeSchema[]>('/api/ci-type-schemas'),
  createCustomField: (input: { ciType: CiType; key: string; label: string; type: CiCustomFieldType; isRequired: boolean; options?: string[]; sortOrder?: number }) =>
    apiRequest<CiCustomField>('/api/ci-custom-fields', { method: 'POST', body: JSON.stringify(input) }),
  deleteCustomField: (id: string) => apiRequest<void>(`/api/ci-custom-fields/${id}`, { method: 'DELETE' }),
}
