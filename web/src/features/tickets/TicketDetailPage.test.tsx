import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci } from '../../api/assets'
import { helpdeskApi, type Ticket, type TicketCiLink } from '../../api/helpdesk'
import { TicketDetailPage } from './TicketDetailPage'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: Object.fromEntries(Object.entries(actual.helpdeskApi).map(([name, value]) => [name, typeof value === 'function' ? vi.fn() : value])) }
})

// WP-5.7 put a problems card on this screen. Mocked to nothing rather than left to hit the real client,
// so a ticket that belongs to no problem is a stated fact of this fixture rather than a failed request.
vi.mock('../../api/problems', async (original) => {
  const actual = await original<typeof import('../../api/problems')>()
  return { ...actual, problemsApi: { ...actual.problemsApi, listForTicket: vi.fn().mockResolvedValue([]) } }
})

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, listCis: vi.fn() } }
})

const laptop: Ci = {
  id: 'ci-1', type: 'Hardware', name: 'LT-4417', assetTag: 'AT-4417', serialNumber: 'SN-4417', description: null,
  isActive: true, lifecycleState: 'Deployed',
  ownership: { ownerUserId: 'user-1', ownerName: 'Requester One', departmentId: null, departmentName: 'Finance', siteId: null, siteName: 'Head Office', assignedAt: '2026-08-01T00:00:00Z' },
  coverage: { contractId: null, contractName: null, contractNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: { manufacturer: 'Dell', model: 'Latitude' }, customFields: [],
  createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z',
}

const link: TicketCiLink = {
  id: 'link-1', ticketId: 'ticket-1', ciId: 'ci-1', ciName: 'LT-4417', ciType: 'Hardware', assetTag: 'AT-4417',
  serialNumber: 'SN-4417', lifecycleState: 'Deployed', isActive: true, ownerName: 'Requester One',
  siteName: 'Head Office', departmentName: 'Finance', warrantyStatus: 'ExpiringSoon',
  warrantyExpiresAt: '2026-08-23', warrantyDaysRemaining: 12, contractName: 'Dell ProSupport',
  openRelatedTickets: [], linkedById: 'tech-1', linkedByName: 'Technician One', linkedAt: '2026-08-07T02:00:00Z',
}

const ticket: Ticket = { id: 'ticket-1', number: 'INC-000001', title: 'VPN unavailable', description: 'Cannot connect', type: 'Incident', urgency: 'High', impact: 'Medium', priority: 'High', status: 'New', requesterId: 'requester-1', requesterName: 'Requester One', queueId: 'queue-1', queueName: 'Service desk', assignedTechnicianId: null, createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T01:00:00Z', categoryId: 'category-laptop', categoryName: 'Laptop issue', customFields: [{ fieldId: 'field-asset-tag', key: 'asset_tag', label: 'Asset tag', type: 'Text', value: 'LT-4417' }] }

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
    vi.mocked(helpdeskApi.getTicketCis).mockResolvedValue([])
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [laptop], total: 1, page: 1, pageSize: 10 })
    vi.mocked(helpdeskApi.listCannedResponses).mockResolvedValue([
      { id: 'canned-1', name: 'Acknowledge receipt', body: 'Hi {{requester.name}}, {{ticket.number}} is with me.', createdById: 'seeder', createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T00:00:00Z' },
      { id: 'canned-2', name: 'Ask for more information', body: 'Hi {{requester.name}}, when did it start?', createdById: 'seeder', createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T00:00:00Z' },
    ])
  })

  it('replaces the previous canned response when a different one is chosen', async () => {
    vi.mocked(helpdeskApi.renderCannedResponse)
      .mockResolvedValueOnce({ id: 'canned-1', name: 'Acknowledge receipt', body: 'Hi Requester One, INC-000001 is with me.' })
      .mockResolvedValueOnce({ id: 'canned-2', name: 'Ask for more information', body: 'Hi Requester One, when did it start?' })
    renderPage()
    const picker = await screen.findByRole('combobox', { name: 'Insert canned response' })

    await userEvent.selectOptions(picker, 'canned-1')
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Comment' })).toHaveValue('Hi Requester One, INC-000001 is with me.'))
    await userEvent.selectOptions(picker, 'canned-2')

    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Comment' })).toHaveValue('Hi Requester One, when did it start?'))
  })

  it('inserts a canned response with its placeholders already filled', async () => {
    vi.mocked(helpdeskApi.renderCannedResponse).mockResolvedValue({ id: 'canned-1', name: 'Acknowledge receipt', body: 'Hi Requester One, INC-000001 is with me.' })
    renderPage()

    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Insert canned response' }), 'canned-1')

    expect(helpdeskApi.renderCannedResponse).toHaveBeenCalledWith('canned-1', 'ticket-1')
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Comment' })).toHaveValue('Hi Requester One, INC-000001 is with me.'))
  })

  it('enables only the legal next transition and distinguishes internal notes', async () => {
    renderPage()
    expect(await screen.findByRole('heading', { name: 'VPN unavailable' })).toBeInTheDocument()
    expect(screen.getByText(/by Requester One/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Triage' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'In progress' })).toBeDisabled()
    expect(screen.getByText('Urgency').parentElement).toHaveTextContent('High')
    expect(screen.getByText('Impact').parentElement).toHaveTextContent('Medium')
    expect(screen.getByText('Category').parentElement).toHaveTextContent('Laptop issue')
    expect(screen.getByText('Asset tag').parentElement).toHaveTextContent('LT-4417')
    expect(screen.getByText('Investigating credentials').closest('li')).toHaveClass('border-amber-400')
    expect(screen.getByText(/Technician One/)).toBeInTheDocument()
    expect(screen.getAllByText('Internal note').length).toBeGreaterThan(0)
    expect(screen.getByRole('option', { name: 'tech-1' })).toBeInTheDocument()
  })

  it('links an asset from the picker and shows it on the ticket', async () => {
    vi.mocked(helpdeskApi.linkTicketCi).mockResolvedValue(link)
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Link an asset' }))
    const dialog = await screen.findByRole('dialog', { name: 'Link an asset' })
    await waitFor(() => expect(within(dialog).getByText('LT-4417')).toBeInTheDocument())

    // The next read of the ticket's links is the one that has to include what was just linked.
    vi.mocked(helpdeskApi.getTicketCis).mockResolvedValue([link])
    await userEvent.click(within(dialog).getByRole('button', { name: 'Link' }))

    await waitFor(() => expect(helpdeskApi.linkTicketCi).toHaveBeenCalledWith('ticket-1', 'ci-1'))
    const card = (await screen.findByRole('heading', { name: 'Linked assets' })).closest('section')!
    await waitFor(() => expect(within(card).getByRole('link', { name: 'LT-4417' })).toHaveAttribute('href', '/assets/ci-1'))
    expect(within(card).getByText(/Linked by Technician One/)).toBeInTheDocument()
  })

  // WP-3.7: the CMDB context an agent reads without leaving the ticket.
  it('shows the linked asset owner, location, warranty and the other tickets open on it', async () => {
    vi.mocked(helpdeskApi.getTicketCis).mockResolvedValue([{
      ...link,
      openRelatedTickets: [{
        ticketId: 'ticket-9', number: 'INC-000031', title: 'Docking station intermittent',
        status: 'InProgress', priority: 'High', createdAt: '2026-08-05T09:00:00Z',
      }],
    }])
    renderPage()

    const card = (await screen.findByRole('heading', { name: 'Linked assets' })).closest('section')!
    expect(await within(card).findByText(/Requester One/)).toBeInTheDocument()
    expect(within(card).getByText(/Head Office/)).toBeInTheDocument()
    expect(within(card).getByText('Warranty expiring soon')).toBeInTheDocument()
    expect(within(card).getByText(/Warranty expires in 12 days/)).toBeInTheDocument()
    expect(within(card).getByText(/Dell ProSupport/)).toBeInTheDocument()
    expect(within(card).getByText('Also open on this asset (1)')).toBeInTheDocument()
    expect(within(card).getByRole('link', { name: 'INC-000031' })).toHaveAttribute('href', '/tickets/ticket-9')
  })

  // An asset with no warranty date must not be reported as though it had one.
  it('says nothing about a warranty the CMDB does not record', async () => {
    vi.mocked(helpdeskApi.getTicketCis).mockResolvedValue([{
      ...link, warrantyStatus: null, warrantyExpiresAt: null, warrantyDaysRemaining: null, contractName: null,
    }])
    renderPage()

    const card = (await screen.findByRole('heading', { name: 'Linked assets' })).closest('section')!
    await within(card).findByRole('link', { name: 'LT-4417' })
    expect(within(card).queryByText(/Warranty/)).not.toBeInTheDocument()
    expect(within(card).queryByText(/Also open on this asset/)).not.toBeInTheDocument()
  })

  it('confirms before unlinking an asset', async () => {
    vi.mocked(helpdeskApi.getTicketCis).mockResolvedValue([link])
    vi.mocked(helpdeskApi.unlinkTicketCi).mockResolvedValue(undefined)
    renderPage()

    const card = (await screen.findByRole('heading', { name: 'Linked assets' })).closest('section')!
    await userEvent.click(await within(card).findByRole('button', { name: 'Unlink' }))

    expect(helpdeskApi.unlinkTicketCi).not.toHaveBeenCalled()
    vi.mocked(helpdeskApi.getTicketCis).mockResolvedValue([])
    await userEvent.click(within(card).getByRole('button', { name: 'Confirm unlink' }))

    await waitFor(() => expect(helpdeskApi.unlinkTicketCi).toHaveBeenCalledWith('ticket-1', 'ci-1'))
    await waitFor(() => expect(within(card).queryByRole('link', { name: 'LT-4417' })).not.toBeInTheDocument())
  })

  it('surfaces a rejected link without closing the picker', async () => {
    vi.mocked(helpdeskApi.linkTicketCi).mockRejectedValue(new Error('CI is already linked to this ticket.'))
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Link an asset' }))
    const dialog = await screen.findByRole('dialog', { name: 'Link an asset' })
    await waitFor(() => expect(within(dialog).getByText('LT-4417')).toBeInTheDocument())
    await userEvent.click(within(dialog).getByRole('button', { name: 'Link' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('CI is already linked to this ticket.')
    expect(screen.getByRole('dialog', { name: 'Link an asset' })).toBeInTheDocument()
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
