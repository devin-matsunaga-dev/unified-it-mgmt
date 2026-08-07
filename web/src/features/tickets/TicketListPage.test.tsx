import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi, type Ticket } from '../../api/helpdesk'
import { TicketListPage } from './TicketListPage'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listTickets: vi.fn(), listQueues: vi.fn(), listCategories: vi.fn(), createTicket: vi.fn() } }
})

const ticket: Ticket = { id: 'ticket-1', number: 'INC-000001', title: 'VPN unavailable', description: 'Cannot connect', type: 'Incident', urgency: 'High', impact: 'Medium', priority: 'High', status: 'New', requesterId: 'requester-1', requesterName: 'Requester One', queueId: null, queueName: null, assignedTechnicianId: null, createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T01:00:00Z', categoryId: null, categoryName: null, customFields: [] }

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><TicketListPage /></QueryClientProvider></MemoryRouter>)
}

describe('TicketListPage', () => {
  beforeEach(() => { vi.clearAllMocks(); localStorage.clear(); vi.mocked(helpdeskApi.listQueues).mockResolvedValue([{ id: 'queue-1', name: 'Service desk', teamId: 'team-1' }]); vi.mocked(helpdeskApi.listCategories).mockResolvedValue([]) })

  it('shows tickets and filters them by text', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
    renderPage()
    expect(await screen.findByText('VPN unavailable')).toBeInTheDocument()
    await userEvent.type(screen.getByRole('textbox', { name: 'Search tickets' }), 'printer')
    expect(screen.getByText('No matching tickets')).toBeInTheDocument()
  })

  it('offers a retry when loading fails', async () => {
    vi.mocked(helpdeskApi.listTickets).mockRejectedValue(new Error('Service unavailable'))
    renderPage()
    expect(await screen.findByRole('alert')).toHaveTextContent('Tickets could not be loaded')
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByText('VPN unavailable')).toBeInTheDocument()
  })

  it('blocks an invalid quick-create submission at the edge', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 })
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'New ticket' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Create ticket' }))
    await waitFor(() => expect(screen.getByText('Enter at least 3 characters.')).toBeInTheDocument())
    expect(helpdeskApi.createTicket).not.toHaveBeenCalled()
  })
})
