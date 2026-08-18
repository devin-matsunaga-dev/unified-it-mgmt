import { apiRequest } from './client'
import type { TicketPriority, TicketType } from './helpdesk'

/**
 * Which days a calendar counts, as the server's [Flags] enum. Sunday is 64 rather than 0 because the
 * flags are a bit set, not a day index.
 */
export const businessDays = [
  { value: 1, label: 'Mon' },
  { value: 2, label: 'Tue' },
  { value: 4, label: 'Wed' },
  { value: 8, label: 'Thu' },
  { value: 16, label: 'Fri' },
  { value: 32, label: 'Sat' },
  { value: 64, label: 'Sun' },
] as const

export type BusinessHoursCalendar = {
  id: string
  name: string
  timeZoneId: string
  /** A bit set over `businessDays`. */
  workingDays: number
  startTime: string
  endTime: string
  /** How many policies measure against it. One with any cannot be deleted. */
  policyCount: number
}

/**
 * One rule in an ordered list. Every condition is optional and null means "any", so a policy with
 * none is the catch-all; the first active policy in order whose conditions all hold is the one a new
 * ticket gets.
 */
export type SlaPolicy = {
  id: string
  name: string
  sortOrder: number
  priority: TicketPriority | null
  ticketType: TicketType | null
  categoryId: string | null
  categoryName: string | null
  responseTargetMinutes: number
  resolutionTargetMinutes: number
  warningPercent: number
  calendarId: string
  calendarName: string
  isActive: boolean
  /** Tickets that have run against it. Any at all means it can be deactivated but not deleted. */
  ticketCount: number
}

export type SavePolicyInput = {
  name: string
  responseTargetMinutes: number
  resolutionTargetMinutes: number
  warningPercent: number
  calendarId: string
  priority?: TicketPriority | null
  ticketType?: TicketType | null
  categoryId?: string | null
  sortOrder?: number
  isActive?: boolean
}

export type SaveCalendarInput = {
  name: string
  timeZoneId: string
  workingDays: number
  startTime: string
  endTime: string
}

export const slaApi = {
  listPolicies: () => apiRequest<SlaPolicy[]>('/api/sla/policies'),
  createPolicy: (input: SavePolicyInput) =>
    apiRequest<SlaPolicy>('/api/sla/policies', { method: 'POST', body: JSON.stringify(input) }),
  updatePolicy: (id: string, input: SavePolicyInput) =>
    apiRequest<SlaPolicy>(`/api/sla/policies/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deletePolicy: (id: string) => apiRequest<void>(`/api/sla/policies/${id}`, { method: 'DELETE' }),
  /** Answers with the whole list in its new order, so the screen never has to guess. */
  reorderPolicies: (policyIds: string[]) =>
    apiRequest<SlaPolicy[]>('/api/sla/policies/order', { method: 'POST', body: JSON.stringify({ policyIds }) }),

  listCalendars: () => apiRequest<BusinessHoursCalendar[]>('/api/sla/calendars'),
  createCalendar: (input: SaveCalendarInput) =>
    apiRequest<BusinessHoursCalendar>('/api/sla/calendars', { method: 'POST', body: JSON.stringify(input) }),
  deleteCalendar: (id: string) => apiRequest<void>(`/api/sla/calendars/${id}`, { method: 'DELETE' }),
}

/** What a policy's conditions read as, for the list. */
export function describePolicyConditions(policy: SlaPolicy): string {
  const parts = [
    policy.priority ?? 'Any priority',
    policy.ticketType === 'ServiceRequest' ? 'Service requests'
      : policy.ticketType === 'Incident' ? 'Incidents'
        : 'Any kind',
  ]
  if (policy.categoryName) parts.push(policy.categoryName)
  return parts.join(' · ')
}

/** Minutes as something readable: targets are entered in minutes but read in hours and days. */
export function describeMinutes(minutes: number): string {
  if (minutes < 60) return `${minutes}m`
  if (minutes % (60 * 24) === 0) return `${minutes / (60 * 24)}d`
  if (minutes % 60 === 0) return `${minutes / 60}h`
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
}
