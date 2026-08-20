import { apiRequest } from './client'
import { filterToQuery } from '../features/tickets/ticketFilters'

export type TicketLevel = 'Low' | 'Medium' | 'High'
export type TicketType = 'Incident' | 'ServiceRequest'
export type TicketPriority = 'Low' | 'Medium' | 'High' | 'Critical'

export type CustomFieldType = 'Text' | 'Number' | 'Date' | 'Select'

export type TicketCustomField = {
  id: string
  categoryId: string
  key: string
  label: string
  type: CustomFieldType
  isRequired: boolean
  options: string[]
  sortOrder: number
}

export type TicketCategory = {
  id: string
  name: string
  parentId: string | null
  isActive: boolean
  sortOrder: number
  fields: TicketCustomField[]
  children: TicketCategory[]
}

/** A new category is always created active; only an update can deactivate one. */
export type CreateTicketCategoryInput = {
  name: string
  parentId: string | null
  sortOrder: number
}

export type UpdateTicketCategoryInput = CreateTicketCategoryInput & { isActive: boolean }

export type TicketCustomFieldValue = {
  fieldId: string
  key: string
  label: string
  type: CustomFieldType
  value: string
}

export type Ticket = {
  id: string
  number: string
  title: string
  description: string
  type: TicketType
  urgency: TicketLevel
  impact: TicketLevel
  priority: TicketPriority
  status: string
  requesterId: string
  requesterName: string
  queueId: string | null
  queueName: string | null
  assignedTechnicianId: string | null
  createdAt: string
  updatedAt: string
  categoryId: string | null
  categoryName: string | null
  customFields: TicketCustomFieldValue[]
  /** Derived from the requester's directory record at read time; null when they are not a known user. */
  requesterDepartmentName: string | null
  requesterSiteName: string | null
}

export type TicketPage = { items: Ticket[]; total: number; page: number; pageSize: number }
export type TicketFilter = {
  search?: string
  statuses?: string[]
  priorities?: TicketPriority[]
  type?: TicketType
  queueId?: string
  assignedTechnicianId?: string
  categoryId?: string
  unassigned?: boolean
  /** The 360° pages: every ticket about one CI, and every ticket raised by one person. */
  ciId?: string
  requesterId?: string
}

/** A CI a ticket is about. Everything but the ids is read live from the CMDB at request time. */
export type TicketCiLink = {
  id: string
  ticketId: string
  ciId: string
  ciName: string
  ciType: string
  assetTag: string | null
  serialNumber: string | null
  lifecycleState: string
  isActive: boolean
  ownerName: string | null
  siteName: string | null
  departmentName: string | null
  /** `Active` | `ExpiringSoon` | `Expired`, or null where no warranty date is recorded. */
  warrantyStatus: string | null
  warrantyExpiresAt: string | null
  /** Negative once the warranty has expired. */
  warrantyDaysRemaining: number | null
  contractName: string | null
  /** Other unfinished tickets about this CI — never the ticket being viewed. */
  openRelatedTickets: RelatedTicket[]
  linkedById: string
  linkedByName: string
  linkedAt: string
}
export type RelatedTicket = {
  ticketId: string
  number: string
  title: string
  status: string
  priority: string
  createdAt: string
}
export type TicketView = {
  id: string
  name: string
  ownerId: string
  ownerName: string
  isShared: boolean
  isMine: boolean
  /** Whether this user may delete it: their own, or anybody's if they are an Admin. */
  canDelete: boolean
  filter: TicketFilter
  createdAt: string
  updatedAt: string
}
export type SaveTicketViewInput = { name: string; isShared: boolean; filter: TicketFilter }
export type CannedResponse = { id: string; name: string; body: string; createdById: string; createdAt: string; updatedAt: string }
export type RenderedCannedResponse = { id: string; name: string; body: string }
export type CreateTicketInput = {
  title: string
  description: string
  type: TicketType
  urgency: TicketLevel
  impact: TicketLevel
  requesterId: string | null
  queueId: string | null
  categoryId: string | null
  customFields: Record<string, string>
  /** Linked as the ticket is created, in one call — see CreateTicketRequest.CiIds on the server. */
  ciIds?: string[]
}
export type Comment = { id: string; ticketId: string; body: string; isInternal: boolean; authorId: string; authorName: string; createdAt: string }
export type Transition = { id: string; ticketId: string; fromStatus: string; toStatus: string; resolutionNote: string | null; actorId: string; occurredAt: string }
export type Assignment = { id: string; ticketId: string; queueId: string; fromTechnicianId: string | null; toTechnicianId: string; kind: number; actorId: string; occurredAt: string }
export type EligibleTechnician = { id: string }
export type TicketQueue = { id: string; name: string; teamId: string }
export type SlaRemaining = {
  ticketId: string
  policy: string
  isPaused: boolean
  responseRemainingSeconds: number
  resolutionRemainingSeconds: number
  responseDueAt: string
  resolutionDueAt: string
  responseCompletedAt: string | null
  resolutionCompletedAt: string | null
}

export const helpdeskApi = {
  listTickets: (filter: TicketFilter = {}) => apiRequest<TicketPage>(`/api/tickets?${filterToQuery(filter)}`),
  listViews: () => apiRequest<TicketView[]>('/api/ticket-views'),
  createView: (input: SaveTicketViewInput) => apiRequest<TicketView>('/api/ticket-views', { method: 'POST', body: JSON.stringify(input) }),
  updateView: (id: string, input: SaveTicketViewInput) => apiRequest<TicketView>(`/api/ticket-views/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteView: (id: string) => apiRequest<void>(`/api/ticket-views/${id}`, { method: 'DELETE' }),
  listCannedResponses: () => apiRequest<CannedResponse[]>('/api/canned-responses'),
  createCannedResponse: (input: { name: string; body: string }) => apiRequest<CannedResponse>('/api/canned-responses', { method: 'POST', body: JSON.stringify(input) }),
  updateCannedResponse: (id: string, input: { name: string; body: string }) => apiRequest<CannedResponse>(`/api/canned-responses/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteCannedResponse: (id: string) => apiRequest<void>(`/api/canned-responses/${id}`, { method: 'DELETE' }),
  renderCannedResponse: (id: string, ticketId: string) => apiRequest<RenderedCannedResponse>(`/api/canned-responses/${id}/render`, { method: 'POST', body: JSON.stringify({ ticketId }) }),
  listQueues: () => apiRequest<TicketQueue[]>('/api/queues'),
  listCategories: () => apiRequest<TicketCategory[]>('/api/ticket-categories'),
  /**
   * Settings only. A separate call rather than a flag on listCategories, because that one is handed
   * straight to TanStack as a queryFn and would receive the query context as its first argument.
   */
  listCategoriesIncludingInactive: () => apiRequest<TicketCategory[]>('/api/ticket-categories?includeInactive=true'),
  createCategory: (input: CreateTicketCategoryInput) => apiRequest<TicketCategory>('/api/ticket-categories', { method: 'POST', body: JSON.stringify(input) }),
  updateCategory: (id: string, input: UpdateTicketCategoryInput) => apiRequest<TicketCategory>(`/api/ticket-categories/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteCategory: (id: string) => apiRequest<void>(`/api/ticket-categories/${id}`, { method: 'DELETE' }),
  getTicket: (id: string) => apiRequest<Ticket>(`/api/tickets/${id}`),
  createTicket: (input: CreateTicketInput) => apiRequest<Ticket>('/api/tickets', { method: 'POST', body: JSON.stringify(input) }),
  getComments: (id: string) => apiRequest<Comment[]>(`/api/tickets/${id}/comments`),
  addComment: (id: string, body: string, isInternal: boolean) => apiRequest<Comment>(`/api/tickets/${id}/comments`, { method: 'POST', body: JSON.stringify({ body, isInternal }) }),
  getTransitions: (id: string) => apiRequest<Transition[]>(`/api/tickets/${id}/transitions`),
  transition: (id: string, targetStatus: string, resolutionNote: string | null) => apiRequest<Ticket>(`/api/tickets/${id}/transitions`, { method: 'POST', body: JSON.stringify({ targetStatus, resolutionNote }) }),
  getAssignments: (id: string) => apiRequest<Assignment[]>(`/api/tickets/${id}/assignments`),
  getEligibleTechnicians: (id: string) => apiRequest<EligibleTechnician[]>(`/api/tickets/${id}/eligible-technicians`),
  assign: (id: string, technicianId: string) => apiRequest<Ticket>(`/api/tickets/${id}/assignments`, { method: 'POST', body: JSON.stringify({ technicianId }) }),
  placeInQueue: (id: string, queueId: string) => apiRequest<Ticket>(`/api/tickets/${id}/queue`, { method: 'POST', body: JSON.stringify({ queueId }) }),
  getSla: (id: string) => apiRequest<SlaRemaining>(`/api/tickets/${id}/sla`),
  getTicketCis: (id: string) => apiRequest<TicketCiLink[]>(`/api/tickets/${id}/cis`),
  linkTicketCi: (id: string, ciId: string) => apiRequest<TicketCiLink>(`/api/tickets/${id}/cis`, { method: 'POST', body: JSON.stringify({ ciId }) }),
  unlinkTicketCi: (id: string, ciId: string) => apiRequest<void>(`/api/tickets/${id}/cis/${ciId}`, { method: 'DELETE' }),
}
