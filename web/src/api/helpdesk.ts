import { apiRequest } from './client'

export type TicketLevel = 'Low' | 'Medium' | 'High'
export type TicketType = 'Incident' | 'ServiceRequest'
export type TicketPriority = 'Low' | 'Medium' | 'High' | 'Critical'

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
}

export type TicketPage = { items: Ticket[]; total: number; page: number; pageSize: number }
export type CreateTicketInput = {
  title: string
  description: string
  type: TicketType
  urgency: TicketLevel
  impact: TicketLevel
  requesterId: string | null
  queueId: string | null
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
  listTickets: () => apiRequest<TicketPage>('/api/tickets?page=1&pageSize=200'),
  listQueues: () => apiRequest<TicketQueue[]>('/api/queues'),
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
}
