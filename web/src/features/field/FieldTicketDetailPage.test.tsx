import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi, type Comment, type Ticket, type TicketCiLink } from '../../api/helpdesk'
import { FieldTicketDetailPage } from './FieldTicketDetailPage'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return {
    ...actual,
    helpdeskApi: {
      ...actual.helpdeskApi,
      getTicket: vi.fn(),
      getComments: vi.fn(),
      getTicketCis: vi.fn(),
      addComment: vi.fn(),
      transition: vi.fn(),
    },
  }
})

const ticket = (over: Partial<Ticket> = {}): Ticket => ({
  id: 't-1', number: 'INC-000001', title: 'VPN unavailable', description: 'Cannot connect from home.',
  type: 'Incident', urgency: 'High', impact: 'Medium', priority: 'High', status: 'New',
  requesterId: 'r-1', requesterName: 'Requester One', queueId: null, queueName: null,
  assignedTechnicianId: 'technician1', createdAt: '2026-08-20T08:00:00Z', updatedAt: '2026-08-20T08:00:00Z',
  categoryId: null, categoryName: null, customFields: [], requesterDepartmentName: null,
  requesterSiteName: null, ...over,
})

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/tickets/t-1']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/tickets/:id" element={<FieldTicketDetailPage />} />
        <Route path="/field/ci/:id" element={<h1>Field asset page</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('FieldTicketDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(helpdeskApi.getTicket).mockResolvedValue(ticket())
    vi.mocked(helpdeskApi.getComments).mockResolvedValue([])
    vi.mocked(helpdeskApi.getTicketCis).mockResolvedValue([])
  })

  it('leads with what is wrong and who reported it', async () => {
    renderPage()

    expect(await screen.findByRole('heading', { name: 'VPN unavailable' })).toBeInTheDocument()
    expect(screen.getByText('INC-000001')).toBeInTheDocument()
    expect(screen.getByText('Requester One')).toBeInTheDocument()
    expect(screen.getByText('Cannot connect from home.')).toBeInTheDocument()
  })

  it('offers the moves a technician makes, excluding the state it is already in', async () => {
    vi.mocked(helpdeskApi.getTicket).mockResolvedValue(ticket({ status: 'InProgress' }))

    renderPage()

    await screen.findByRole('heading', { name: 'VPN unavailable' })
    expect(screen.queryByRole('button', { name: 'Start work' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Park it' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Resolve' })).toBeInTheDocument()
  })

  it('moves the ticket straight through for a status needing no note', async () => {
    vi.mocked(helpdeskApi.transition).mockResolvedValue(ticket({ status: 'InProgress' }))

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Start work' }))

    await waitFor(() => expect(helpdeskApi.transition).toHaveBeenCalledWith('t-1', 'InProgress', null))
  })

  /** The server refuses a resolve with no note, so the button opens a field rather than firing a 400. */
  it('asks what fixed it before resolving, and will not send without one', async () => {
    vi.mocked(helpdeskApi.transition).mockResolvedValue(ticket({ status: 'Resolved' }))

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Resolve' }))

    expect(screen.getByLabelText('What fixed it?')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirm resolve' })).toBeDisabled()
    expect(helpdeskApi.transition).not.toHaveBeenCalled()

    await userEvent.type(screen.getByLabelText('What fixed it?'), 'Reissued the certificate.')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm resolve' }))

    await waitFor(() => expect(helpdeskApi.transition).toHaveBeenCalledWith('t-1', 'Resolved', 'Reissued the certificate.'))
  })

  it('adds a work note as internal, because it is for whoever picks the ticket up', async () => {
    vi.mocked(helpdeskApi.addComment).mockResolvedValue({ id: 'c-1' } as Comment)

    renderPage()
    await userEvent.type(await screen.findByLabelText('Add a work note'), 'Swapped the dock.')
    await userEvent.click(screen.getByRole('button', { name: /Add note/ }))

    await waitFor(() => expect(helpdeskApi.addComment).toHaveBeenCalledWith('t-1', 'Swapped the dock.', true))
  })

  it('links a linked asset to its field screen, not the desktop one', async () => {
    vi.mocked(helpdeskApi.getTicketCis).mockResolvedValue([
      { id: 'l-1', ticketId: 't-1', ciId: 'ci-9', ciName: 'Reception laptop', assetTag: 'LT-00421' } as TicketCiLink,
    ])

    renderPage()

    expect(await screen.findByRole('link', { name: /Reception laptop/ })).toHaveAttribute('href', '/field/ci/ci-9')
  })

  it('says so when the ticket cannot be loaded', async () => {
    vi.mocked(helpdeskApi.getTicket).mockRejectedValue(new Error('Network request failed'))

    renderPage()

    expect(await screen.findByRole('alert')).toHaveTextContent('Ticket not found')
  })
})
