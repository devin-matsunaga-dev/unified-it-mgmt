import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi, type Ticket, type TicketView } from '../../api/helpdesk'
import { TicketListPage } from './TicketListPage'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listTickets: vi.fn(), listQueues: vi.fn(), listCategories: vi.fn(), createTicket: vi.fn(), listViews: vi.fn(), createView: vi.fn(), updateView: vi.fn(), deleteView: vi.fn() } }
})

const ticket: Ticket = { id: 'ticket-1', number: 'INC-000001', title: 'VPN unavailable', description: 'Cannot connect', type: 'Incident', urgency: 'High', impact: 'Medium', priority: 'High', status: 'New', requesterId: 'requester-1', requesterName: 'Requester One', queueId: null, queueName: null, assignedTechnicianId: null, createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T01:00:00Z', categoryId: null, categoryName: null, customFields: [], requesterDepartmentName: null, requesterSiteName: null }

const savedView: TicketView = { id: 'view-1', name: 'Unassigned high priority', ownerId: 'tech-1', ownerName: 'Technician One', isShared: true, isMine: false, filter: { priorities: ['High'], unassigned: true }, createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T00:00:00Z' }

function renderPage(entry = '/tickets') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={[entry]}><QueryClientProvider client={client}><TicketListPage /></QueryClientProvider></MemoryRouter>)
}

describe('TicketListPage', () => {
  beforeEach(() => { vi.clearAllMocks(); localStorage.clear(); vi.mocked(helpdeskApi.listQueues).mockResolvedValue([{ id: 'queue-1', name: 'Service desk', teamId: 'team-1' }]); vi.mocked(helpdeskApi.listCategories).mockResolvedValue([]); vi.mocked(helpdeskApi.listViews).mockResolvedValue([]) })

  it('sends the typed search term to the API and shows the empty state when nothing matches', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
    renderPage()
    expect(await screen.findByText('VPN unavailable')).toBeInTheDocument()

    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 })
    await userEvent.type(screen.getByRole('textbox', { name: 'Search tickets' }), 'printer')

    await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith(expect.objectContaining({ search: 'printer' })))
    expect(await screen.findByText('No matching tickets')).toBeInTheDocument()
  })

  it('applies a saved view and saves the current filters as a new view', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
    vi.mocked(helpdeskApi.listViews).mockResolvedValue([savedView])
    vi.mocked(helpdeskApi.createView).mockResolvedValue({ ...savedView, id: 'view-2', name: 'Pending work' })
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Unassigned high priority' }))
    await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith(expect.objectContaining({ priorities: ['High'], unassigned: true })))

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by status' }), 'Pending')
    await userEvent.click(screen.getByRole('button', { name: 'Save view' }))
    await userEvent.type(within(screen.getByRole('dialog')).getByRole('textbox'), 'Pending work')
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Save view' }))

    await waitFor(() => expect(helpdeskApi.createView).toHaveBeenCalledWith({
      name: 'Pending work', isShared: false,
      filter: { statuses: ['Pending'], priorities: ['High'], unassigned: true },
    }))
  })

  it('offers a retry when loading fails', async () => {
    vi.mocked(helpdeskApi.listTickets).mockRejectedValue(new Error('Service unavailable'))
    renderPage()
    expect(await screen.findByRole('alert')).toHaveTextContent('Tickets could not be loaded')
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByText('VPN unavailable')).toBeInTheDocument()
  })

  /**
   * The receiving half of a WP-5.5 dashboard deep link: a widget's Critical band opens this list already
   * narrowed. Asserted on what reaches the API, because a filter the table applied but the query did not
   * would show one page of the right rows and the wrong total.
   */
  it('opens narrowed to the priority a deep link names', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
    renderPage('/tickets?priority=Critical')
    await waitFor(() => expect(helpdeskApi.listTickets)
      .toHaveBeenCalledWith(expect.objectContaining({ priorities: ['Critical'] })))
  })

  it('ignores a deep link asking for a priority that does not exist', async () => {
    // A link that narrowed the list to nothing looks exactly like a broken screen, so an unrecognised
    // value is dropped rather than sent.
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
    renderPage('/tickets?priority=Apocalyptic')
    await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalled())
    expect(vi.mocked(helpdeskApi.listTickets).mock.calls[0][0]).not.toHaveProperty('priorities')
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

/**
 * The quick switch between the two kinds. It exists because alert-raised incidents and things people
 * asked for sit in one list, and the type filter had a server and a serialiser but no control.
 */
describe('the incident / service request switch', () => {
  it('filters the list to one kind and back to all', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
    const user = userEvent.setup()

    renderPage()
    await screen.findByText('VPN unavailable')

    await user.click(screen.getByRole('button', { name: 'Service requests' }))
    await waitFor(() => expect(helpdeskApi.listTickets)
      .toHaveBeenLastCalledWith(expect.objectContaining({ type: 'ServiceRequest' })))
    expect(screen.getByRole('button', { name: 'Service requests' })).toHaveAttribute('aria-pressed', 'true')

    await user.click(screen.getByRole('button', { name: 'Incidents' }))
    await waitFor(() => expect(helpdeskApi.listTickets)
      .toHaveBeenLastCalledWith(expect.objectContaining({ type: 'Incident' })))

    // "All" must clear the member rather than send a value, or the filter never round-trips to empty.
    await user.click(screen.getByRole('button', { name: 'All' }))
    await waitFor(() => expect(helpdeskApi.listTickets)
      .toHaveBeenLastCalledWith(expect.not.objectContaining({ type: expect.anything() })))
  })
})
