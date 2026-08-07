import type { Ticket } from '../../api/helpdesk'

export const portalRequest: Ticket = {
  id: 'ticket-1',
  number: 'INC-000001',
  title: 'VPN unavailable',
  description: 'Cannot connect from home.',
  type: 'Incident',
  urgency: 'High',
  impact: 'Medium',
  priority: 'High',
  status: 'Resolved',
  requesterId: 'requester-1',
  requesterName: 'Requester One',
  queueId: 'queue-1',
  queueName: 'Service Desk',
  assignedTechnicianId: 'technician1',
  createdAt: '2026-08-07T00:00:00Z',
  updatedAt: '2026-08-07T01:00:00Z',
}
