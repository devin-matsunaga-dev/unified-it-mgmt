import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi, type Ticket } from '../../api/helpdesk'
import { TicketDetailPage } from './TicketDetailPage'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: Object.fromEntries(Object.entries(actual.helpdeskApi).map(([name, value]) => [name, typeof value === 'function' ? vi.fn() : value])) }
})

const ticket: Ticket = { id: 'ticket-1', number: 'INC-000001', title: 'VPN unavailable', description: 'Cannot connect', type: 'Incident', urgency: 'High', impact: 'Medium', priority: 'High', status: 'New', requesterId: 'requester-1', requesterName: 'Requester One', queueId: 'queue-1', queueName: 'Service desk', assignedTechnicianId: null, createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T01:00:00Z' }

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/tickets/ticket-1']}><QueryClientProvider client={client}><Routes><Route path="/tickets/:id" element={<TicketDetailPage />} /></Routes></QueryClientProvider></MemoryRouter>)
}

describe('TicketDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(helpdeskApi.getTicket).mockResolvedValue(ticket)
    vi.mocked(helpdeskApi.listQueues).mockResolvedValue([{ id: 'queue-1', name: 'Service desk', teamId: 'team-1' }])
    vi.mocked(helpdeskApi.getComments).mockResolvedValue([{ id: 'comment-1', ticketId: ticket.id, body: 'Investigating credentials', isInternal: true, authorId: 'tech-1', authorName: 'Technician One', createdAt: '2026-08-07T01:30:00Z' }])
    vi.mocked(helpdeskApi.getTransitions).mockResolvedValue([])
    vi.mocked(helpdeskApi.getAssignments).mockResolvedValue([])
    vi.mocked(helpdeskApi.getEligibleTechnicians).mockResolvedValue([{ id: 'tech-1' }, { id: 'tech-2' }])
    vi.mocked(helpdeskApi.placeInQueue).mockResolvedValue(ticket)
    vi.mocked(helpdeskApi.getSla).mockRejectedValue(new Error('No SLA'))
  })

  it('enables only the legal next transition and distinguishes internal notes', async () => {
    renderPage()
    expect(await screen.findByRole('heading', { name: 'VPN unavailable' })).toBeInTheDocument()
    expect(screen.getByText(/by Requester One/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Triage' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'In progress' })).toBeDisabled()
    expect(screen.getByText('Urgency').parentElement).toHaveTextContent('High')
    expect(screen.getByText('Impact').parentElement).toHaveTextContent('Medium')
    expect(screen.getByText('Investigating credentials').closest('li')).toHaveClass('border-amber-400')
    expect(screen.getByText(/Technician One/)).toBeInTheDocument()
    expect(screen.getAllByText('Internal note').length).toBeGreaterThan(0)
    expect(screen.getByRole('option', { name: 'tech-1' })).toBeInTheDocument()
  })

  it('does not request eligible technicians for an unqueued ticket', async () => {
    vi.mocked(helpdeskApi.getTicket).mockResolvedValue({ ...ticket, queueId: null, queueName: null })

    renderPage()

    expect(await screen.findByText('Select a queue before assigning a technician.')).toBeInTheDocument()
    expect(helpdeskApi.getEligibleTechnicians).not.toHaveBeenCalled()
    expect(screen.queryByText('Eligible technicians could not be loaded.')).not.toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Ticket queue' }), 'queue-1')
    await waitFor(() => expect(helpdeskApi.placeInQueue).toHaveBeenCalledWith('ticket-1', 'queue-1'))
  })
})
