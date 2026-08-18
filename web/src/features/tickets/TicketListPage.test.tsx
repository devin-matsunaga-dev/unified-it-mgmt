import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { directoryApi } from '../../api/directory'
import { vi } from 'vitest'
import { helpdeskApi, type Ticket, type TicketView } from '../../api/helpdesk'
import { TicketListPage } from './TicketListPage'

/** Signed in as a technician, so the view that is about the reader is offered. */
vi.mock('../../auth/AuthProvider', () => ({
  useAuth: () => ({
    user: null,
    account: { id: 'sub-1', name: 'Technician One', username: 'technician1', email: null, roles: ['Technician'] },
    roles: ['Technician'],
    isLoading: false,
    signIn: vi.fn(),
    signOut: vi.fn(),
  }),
}))

vi.mock('../../api/directory', () => ({
  directoryApi: { listUsers: vi.fn(), listDepartments: vi.fn(), listSites: vi.fn() },
}))

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listTickets: vi.fn(), listQueues: vi.fn(), listCategories: vi.fn(), createTicket: vi.fn(), listViews: vi.fn(), createView: vi.fn(), updateView: vi.fn(), deleteView: vi.fn() } }
})

const ticket: Ticket = { id: 'ticket-1', number: 'INC-000001', title: 'VPN unavailable', description: 'Cannot connect', type: 'Incident', urgency: 'High', impact: 'Medium', priority: 'High', status: 'New', requesterId: 'requester-1', requesterName: 'Requester One', queueId: null, queueName: null, assignedTechnicianId: null, createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T01:00:00Z', categoryId: null, categoryName: null, customFields: [], requesterDepartmentName: null, requesterSiteName: null }

const savedView: TicketView = { id: 'view-1', name: 'Escalations this week', ownerId: 'tech-1', ownerName: 'Technician One', isShared: true, isMine: false, canDelete: false, filter: { priorities: ['High'], unassigned: true }, createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T00:00:00Z' }

function renderPage(entry = '/tickets') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={[entry]}><QueryClientProvider client={client}><TicketListPage /></QueryClientProvider></MemoryRouter>)
}

/** Two technicians and an end user, so the assignee list can be shown to exclude the latter. */
const people = [
  { id: 'u1', username: 'technician1', displayName: 'Technician One', email: 't1@example.test', role: 'Technician', siteId: 's1', siteName: 'HQ', departmentId: 'd1', departmentName: 'IT' },
  { id: 'u2', username: 'manager1', displayName: 'Manager One', email: 'm1@example.test', role: 'Manager', siteId: 's1', siteName: 'HQ', departmentId: 'd1', departmentName: 'IT' },
  { id: 'u3', username: 'enduser1', displayName: 'End User One', email: 'e1@example.test', role: 'EndUser', siteId: 's1', siteName: 'HQ', departmentId: 'd2', departmentName: 'Finance' },
]

describe('TicketListPage', () => {
  beforeEach(() => { vi.clearAllMocks(); localStorage.clear(); vi.mocked(directoryApi.listUsers).mockResolvedValue(people); vi.mocked(helpdeskApi.listQueues).mockResolvedValue([{ id: 'queue-1', name: 'Service desk', teamId: 'team-1' }]); vi.mocked(helpdeskApi.listCategories).mockResolvedValue([]); vi.mocked(helpdeskApi.listViews).mockResolvedValue([]) })

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

    await userEvent.click(await screen.findByRole('button', { name: 'Escalations this week' }))
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
  describe('choosing which filters show', () => {
    it('hides a filter control that is turned off, and keeps the search box', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      expect(screen.getByLabelText('Filter by priority')).toBeInTheDocument()

      await user.click(screen.getByRole('button', { name: 'Filters' }))
      await user.click(screen.getByRole('checkbox', { name: 'Priority' }))

      expect(screen.queryByLabelText('Filter by priority')).not.toBeInTheDocument()
      expect(screen.getByPlaceholderText(/Search titles/)).toBeInTheDocument()
    })

    /** A list still narrowed by a control nobody can see is a subset with no visible reason. */
    it('clears what a filter was narrowing by when that filter is hidden', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      await user.selectOptions(screen.getByLabelText('Filter by priority'), 'High')
      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith(
        expect.objectContaining({ priorities: ['High'] })))

      await user.click(screen.getByRole('button', { name: 'Filters' }))
      await user.click(screen.getByRole('checkbox', { name: 'Priority' }))

      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenLastCalledWith(
        expect.objectContaining({ priorities: undefined })))
    })

    /** The incident / service request switch is a filter like any other and can be put away. */
    it('can hide the kind switch and clears the kind with it', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      await user.selectOptions(screen.getByLabelText('Filter by kind'), 'ServiceRequest')
      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith(
        expect.objectContaining({ type: 'ServiceRequest' })))

      await user.click(screen.getByRole('button', { name: 'Filters' }))
      await user.click(screen.getByRole('checkbox', { name: 'Incident / service request' }))

      expect(screen.queryByLabelText('Filter by kind')).not.toBeInTheDocument()
      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenLastCalledWith(
        expect.objectContaining({ type: undefined })))
    })

    it('remembers which filters are shown across a remount', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      const first = renderPage()
      await screen.findByText('VPN unavailable')

      await user.click(screen.getByRole('button', { name: 'Filters' }))
      await user.click(screen.getByRole('checkbox', { name: 'Queue' }))
      first.unmount()

      renderPage()
      await screen.findByText('VPN unavailable')

      expect(screen.queryByLabelText('Filter by queue')).not.toBeInTheDocument()
    })
  })

  describe('the default views', () => {
    it('offers all four, and applies one when clicked', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      // My tickets sits directly after All tickets, which is where the eye goes first.
      const chips = screen.getAllByRole('button', { name: /^(All tickets|My tickets|Unassigned high priority|Needs triage|Awaiting customer)$/ })
      expect(chips.map((chip) => chip.textContent))
        .toEqual(['All tickets', 'My tickets', 'Unassigned high priority', 'Needs triage', 'Awaiting customer'])

      await user.click(screen.getByRole('button', { name: 'Needs triage' }))

      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenLastCalledWith(
        expect.objectContaining({ statuses: ['New', 'Triage'] })))
      expect(screen.getByRole('button', { name: 'Needs triage' })).toHaveAttribute('aria-pressed', 'true')
    })

    /** The whole point: a default nobody can remove is not a default. */
    it('removes a default and offers it back from the menu', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      await user.click(screen.getByRole('button', { name: 'Remove All tickets' }))
      expect(screen.queryByRole('button', { name: 'All tickets' })).not.toBeInTheDocument()

      await user.click(screen.getByRole('button', { name: 'Default views' }))
      const entry = screen.getByRole('checkbox', { name: /All tickets/ })
      expect(entry).not.toBeChecked()

      await user.click(entry)
      expect(screen.getByRole('button', { name: 'All tickets' })).toBeInTheDocument()
    })

    it('remembers which defaults were removed across a remount', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      const first = renderPage()
      await screen.findByText('VPN unavailable')

      await user.click(screen.getByRole('button', { name: 'Remove Unassigned high priority' }))
      first.unmount()

      renderPage()
      await screen.findByText('VPN unavailable')

      expect(screen.queryByRole('button', { name: 'Unassigned high priority' })).not.toBeInTheDocument()
    })

    it('reorders the view chips when one is dropped on another, and remembers it', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const first = renderPage()
      await screen.findByText('VPN unavailable')

      const chipOrder = () => screen.getAllByRole('button', { name: /^(All tickets|My tickets|Unassigned high priority|Needs triage|Awaiting customer)$/ })
        .map((chip) => chip.textContent)
      expect(chipOrder()[0]).toBe('All tickets')

      const grab = (label: string) => screen.getByRole('button', { name: label }).closest('[draggable]')!
      fireEvent.dragStart(grab('Awaiting customer'))
      fireEvent.dragOver(grab('All tickets'))
      fireEvent.drop(grab('All tickets'))

      expect(chipOrder()[0]).toBe('Awaiting customer')
      expect(chipOrder()[1]).toBe('All tickets')
      first.unmount()

      renderPage()
      await screen.findByText('VPN unavailable')
      expect(chipOrder()[0]).toBe('Awaiting customer')
    })

    /** Saved views share the arrangement, so a default can be dragged past one and stay there. */
    it('arranges saved views alongside the defaults', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      vi.mocked(helpdeskApi.listViews).mockResolvedValue([savedView])
      renderPage()
      await screen.findByRole('button', { name: 'Escalations this week' })

      const grab = (label: string) => screen.getByRole('button', { name: label }).closest('[draggable]')!
      fireEvent.dragStart(grab('Escalations this week'))
      fireEvent.dragOver(grab('All tickets'))
      fireEvent.drop(grab('All tickets'))

      const all = screen.getAllByRole('button', { name: /^(All tickets|Escalations this week)$/ })
        .map((chip) => chip.textContent)
      expect(all[0]).toBe('Escalations this week')
    })

    /** A default that has been removed keeps its place for when it is brought back. */
    it('keeps a dragged default in its place after being removed and restored', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      const grab = (label: string) => screen.getByRole('button', { name: label }).closest('[draggable]')!
      fireEvent.dragStart(grab('Needs triage'))
      fireEvent.dragOver(grab('All tickets'))
      fireEvent.drop(grab('All tickets'))

      await user.click(screen.getByRole('button', { name: 'Remove Needs triage' }))
      await user.click(screen.getByRole('button', { name: 'Default views' }))
      await user.click(screen.getByRole('checkbox', { name: /Needs triage/ }))

      const chipOrder = () => screen.getAllByRole('button', { name: /^(All tickets|My tickets|Unassigned high priority|Needs triage|Awaiting customer)$/ })
        .map((chip) => chip.textContent)
      expect(chipOrder()[0]).toBe('Needs triage')
    })

    /** Matched on the username: a ticket records the identity the helpdesk was given. */
    it('filters My tickets by the signed-in username', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      await user.click(screen.getByRole('button', { name: 'My tickets' }))

      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenLastCalledWith(
        expect.objectContaining({ assignedTechnicianId: 'technician1' })))
    })

    /** A saved view is a record somebody owns; it is deleted, never hidden with the defaults. */
    it('does not offer to hide a saved view', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      vi.mocked(helpdeskApi.listViews).mockResolvedValue([savedView])
      renderPage()
      await screen.findByText('VPN unavailable')

      expect(await screen.findByRole('button', { name: 'Escalations this week' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Remove Escalations this week' })).not.toBeInTheDocument()
    })

    /** Every default can go; the menu that brings them back does not live among them. */
    it('can have every default removed', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      for (const label of ['All tickets', 'My tickets', 'Unassigned high priority', 'Needs triage', 'Awaiting customer']) {
        await user.click(screen.getByRole('button', { name: `Remove ${label}` }))
      }

      // Every one of them, including the last: the menu that brings them back is not among them.
      // The floor that stops the final column being hidden is deliberately absent here.
      for (const label of ['All tickets', 'My tickets', 'Unassigned high priority', 'Needs triage', 'Awaiting customer']) {
        expect(screen.queryByRole('button', { name: label })).not.toBeInTheDocument()
      }
      expect(screen.getByRole('button', { name: 'Default views' })).toBeInTheDocument()
    })
  })

  describe('the assignee filter', () => {
    /** End users raise tickets but never take them, so listing them would bury the real assignees. */
    it('offers only the people who take work, plus unassigned', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      renderPage()
      await screen.findByText('VPN unavailable')

      const control = await screen.findByLabelText('Filter by assignee')
      expect([...control.querySelectorAll('option')].map((option) => option.textContent))
        .toEqual(['Anyone', 'Unassigned', 'Manager One', 'Technician One'])
    })

    /**
     * The value is the username, not the user id: a ticket records the identity the helpdesk was
     * given, and the people page filters by it the same way.
     */
    it('filters by a chosen person using their username', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      await user.selectOptions(await screen.findByLabelText('Filter by assignee'), 'technician1')

      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith(
        expect.objectContaining({ assignedTechnicianId: 'technician1', unassigned: undefined })))
    })

    it('filters to tickets nobody holds', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      await user.selectOptions(await screen.findByLabelText('Filter by assignee'), 'unassigned')

      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith(
        expect.objectContaining({ unassigned: true, assignedTechnicianId: undefined })))
    })

    /**
     * The two halves are mutually exclusive in normalizeFilter — unassigned wins — so one control
     * has to clear the other rather than leaving a contradiction behind.
     */
    it('switching from unassigned to a person clears the unassigned half', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      const control = await screen.findByLabelText('Filter by assignee')
      await user.selectOptions(control, 'unassigned')
      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith(
        expect.objectContaining({ unassigned: true })))

      await user.selectOptions(control, 'manager1')

      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenLastCalledWith(
        expect.objectContaining({ assignedTechnicianId: 'manager1', unassigned: undefined })))
    })

    it('goes back to anyone', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      const control = await screen.findByLabelText('Filter by assignee')
      await user.selectOptions(control, 'technician1')
      await user.selectOptions(control, '')

      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenLastCalledWith(
        expect.objectContaining({ assignedTechnicianId: undefined, unassigned: undefined })))
    })

    /** Hiding the control owns the whole question, so both halves go with it. */
    it('clears both halves when the control is hidden', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      await user.selectOptions(await screen.findByLabelText('Filter by assignee'), 'unassigned')
      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith(
        expect.objectContaining({ unassigned: true })))

      await user.click(screen.getByRole('button', { name: 'Filters' }))
      await user.click(screen.getByRole('checkbox', { name: 'Assigned to' }))

      expect(screen.queryByLabelText('Filter by assignee')).not.toBeInTheDocument()
      await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenLastCalledWith(
        expect.objectContaining({ unassigned: undefined, assignedTechnicianId: undefined })))
    })
  })

  describe('rearranging the columns', () => {
    it('reorders when a heading is dropped on another, and remembers it', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const first = renderPage()
      await screen.findByText('VPN unavailable')

      const headings = () => screen.getAllByRole('columnheader').map((cell) => cell.textContent?.trim())
      expect(headings()[0]).toContain('ID')
      expect(headings()[1]).toContain('Title')

      fireEvent.dragStart(screen.getByRole('columnheader', { name: /Priority/ }))
      fireEvent.dragOver(screen.getByRole('columnheader', { name: /ID/ }))
      fireEvent.drop(screen.getByRole('columnheader', { name: /ID/ }))

      expect(headings()[0]).toContain('Priority')
      expect(headings()[1]).toContain('ID')
      first.unmount()

      renderPage()
      await screen.findByText('VPN unavailable')
      expect(headings()[0]).toContain('Priority')
    })

    /** Hiding and ordering are separate: a hidden column keeps its place for when it returns. */
    it('keeps a dragged column in its new place after being hidden and shown again', async () => {
      vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
      const user = userEvent.setup()
      renderPage()
      await screen.findByText('VPN unavailable')

      fireEvent.dragStart(screen.getByRole('columnheader', { name: /Priority/ }))
      fireEvent.dragOver(screen.getByRole('columnheader', { name: /ID/ }))
      fireEvent.drop(screen.getByRole('columnheader', { name: /ID/ }))

      await user.click(screen.getByRole('button', { name: 'Columns' }))
      await user.click(screen.getByRole('checkbox', { name: 'Priority' }))
      await user.click(screen.getByRole('checkbox', { name: 'Priority' }))

      expect(screen.getAllByRole('columnheader')[0].textContent).toContain('Priority')
    })
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

    const kind = screen.getByLabelText('Filter by kind')
    await user.selectOptions(kind, 'ServiceRequest')
    await waitFor(() => expect(helpdeskApi.listTickets)
      .toHaveBeenLastCalledWith(expect.objectContaining({ type: 'ServiceRequest' })))
    expect(kind).toHaveValue('ServiceRequest')

    await user.selectOptions(kind, 'Incident')
    await waitFor(() => expect(helpdeskApi.listTickets)
      .toHaveBeenLastCalledWith(expect.objectContaining({ type: 'Incident' })))

    // "All kinds" must clear the member rather than send a value, or the filter never round-trips
    // back to empty and a saved view would carry a type nobody chose.
    await user.selectOptions(kind, '')
    await waitFor(() => expect(helpdeskApi.listTickets)
      .toHaveBeenLastCalledWith(expect.not.objectContaining({ type: expect.anything() })))
  })

})
