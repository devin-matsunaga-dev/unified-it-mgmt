import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi, type Ticket, type TicketPage } from '../../api/helpdesk'
import { FieldTicketsPage, waitingFor } from './FieldTicketsPage'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listTickets: vi.fn() } }
})
vi.mock('../../auth/AuthProvider', () => ({
  useAuth: () => ({ account: { id: 'oidc-sub', name: 'Tech One', username: 'technician1', email: null, roles: ['Technician'] } }),
}))

const ticket = (over: Partial<Ticket>): Ticket => ({
  id: 't-1', number: 'INC-000001', title: 'VPN unavailable', description: '', type: 'Incident',
  urgency: 'High', impact: 'Medium', priority: 'High', status: 'New', requesterId: 'r-1',
  requesterName: 'Requester One', queueId: null, queueName: null, assignedTechnicianId: 'technician1',
  createdAt: '2026-08-20T08:00:00Z', updatedAt: '2026-08-20T08:00:00Z', categoryId: null,
  categoryName: null, customFields: [], requesterDepartmentName: null, requesterSiteName: null, ...over,
})

const page = (items: Ticket[]): TicketPage => ({ items, total: items.length, page: 1, pageSize: 25 })

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/tickets']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/tickets" element={<FieldTicketsPage />} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('waitingFor', () => {
  const now = new Date('2026-08-20T12:00:00Z')

  it('uses the coarsest unit that is still true', () => {
    expect(waitingFor('2026-08-20T11:30:00Z', now)).toBe('30m old')
    expect(waitingFor('2026-08-20T06:00:00Z', now)).toBe('6h old')
    expect(waitingFor('2026-08-17T12:00:00Z', now)).toBe('3d old')
  })

  it('never reports a negative age for a clock that is slightly ahead', () => {
    expect(waitingFor('2026-08-20T12:05:00Z', now)).toBe('0m old')
  })
})

describe('FieldTicketsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue(page([ticket({})]))
  })

  it('opens on the technician\'s own open work, scoped by sign-in name', async () => {
    renderPage()

    await screen.findByText('VPN unavailable')
    expect(helpdeskApi.listTickets).toHaveBeenCalledWith({
      statuses: ['New', 'Triage', 'InProgress', 'Pending'],
      assignedTechnicianId: 'technician1',
    })
  })

  it('drops the assignee filter when all open work is asked for', async () => {
    renderPage()
    await screen.findByText('VPN unavailable')

    await userEvent.click(screen.getByRole('button', { name: 'All open' }))

    await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenLastCalledWith({
      statuses: ['New', 'Triage', 'InProgress', 'Pending'],
    }))
  })

  it('links a row to its field detail, not the desktop page', async () => {
    renderPage()

    expect(await screen.findByRole('link', { name: /VPN unavailable/ })).toHaveAttribute('href', '/field/tickets/t-1')
  })

  it('says which list is empty rather than showing a bare nothing', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue(page([]))

    renderPage()

    expect(await screen.findByText('Nothing is assigned to you right now.')).toBeInTheDocument()
  })
})
