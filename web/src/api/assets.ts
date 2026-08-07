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

export type CiLifecycleState = 'Ordered' | 'InStock' | 'Deployed' | 'InRepair' | 'Retired' | 'Disposed'
export type CiAssignmentAction = 'CheckOut' | 'CheckIn' | 'Transfer' | 'Relocate'

/** Who holds a CI and where it lives. Names are snapshots taken when it was assigned. */
export type CiOwnership = {
  ownerUserId: string | null
  ownerName: string | null
  departmentId: string | null
  departmentName: string | null
  siteId: string | null
  siteName: string | null
  assignedAt: string | null
}

export type CiLifecycleHistory = {
  id: string
  ciId: string
  fromState: CiLifecycleState
  toState: CiLifecycleState
  note: string | null
  actorId: string
  occurredAt: string
}

export type CiAssignmentEntry = {
  id: string
  ciId: string
  action: CiAssignmentAction
  fromOwnerUserId: string | null
  fromOwnerName: string | null
  toOwnerUserId: string | null
  toOwnerName: string | null
  departmentId: string | null
  departmentName: string | null
  siteId: string | null
  siteName: string | null
  note: string | null
  actorId: string
  occurredAt: string
}

/** The lifecycle graph as data: the server owns the guard, the form only renders it. */
export type CiLifecycleStateInfo = { state: CiLifecycleState; allowedTargets: CiLifecycleState[] }

export type Ci = {
  id: string
  type: CiType
  name: string
  assetTag: string | null
  serialNumber: string | null
  description: string | null
  isActive: boolean
  lifecycleState: CiLifecycleState
  ownership: CiOwnership
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
  lifecycleState?: CiLifecycleState
  ownerUserId?: string
  departmentId?: string
  siteId?: string
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
  lifecycleState?: CiLifecycleState
}

export type AssignCiInput = {
  ownerUserId: string | null
  departmentId: string | null
  siteId: string | null
  note: string | null
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
  if (filter.lifecycleState) query.set('lifecycleState', filter.lifecycleState)
  if (filter.ownerUserId) query.set('ownerUserId', filter.ownerUserId)
  if (filter.departmentId) query.set('departmentId', filter.departmentId)
  if (filter.siteId) query.set('siteId', filter.siteId)
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
  listLifecycleStates: () => apiRequest<CiLifecycleStateInfo[]>('/api/ci-lifecycle-states'),
  transitionCi: (id: string, targetState: CiLifecycleState, note: string | null) =>
    apiRequest<Ci>(`/api/cis/${id}/lifecycle-transitions`, { method: 'POST', body: JSON.stringify({ targetState, note }) }),
  getLifecycleHistory: (id: string) => apiRequest<CiLifecycleHistory[]>(`/api/cis/${id}/lifecycle-transitions`),
  assignCi: (id: string, input: AssignCiInput) => apiRequest<Ci>(`/api/cis/${id}/assignment`, { method: 'PUT', body: JSON.stringify(input) }),
  getAssignments: (id: string) => apiRequest<CiAssignmentEntry[]>(`/api/cis/${id}/assignments`),
  listTypeSchemas: () => apiRequest<CiTypeSchema[]>('/api/ci-type-schemas'),
  createCustomField: (input: { ciType: CiType; key: string; label: string; type: CiCustomFieldType; isRequired: boolean; options?: string[]; sortOrder?: number }) =>
    apiRequest<CiCustomField>('/api/ci-custom-fields', { method: 'POST', body: JSON.stringify(input) }),
  deleteCustomField: (id: string) => apiRequest<void>(`/api/ci-custom-fields/${id}`, { method: 'DELETE' }),
}
