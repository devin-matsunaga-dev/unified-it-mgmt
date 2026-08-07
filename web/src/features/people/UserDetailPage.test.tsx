import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci } from '../../api/assets'
import { directoryApi, type DirectoryUser } from '../../api/directory'
import { helpdeskApi, type Ticket } from '../../api/helpdesk'
import { UserDetailPage } from './UserDetailPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, listCis: vi.fn() } }
})

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listTickets: vi.fn() } }
})

vi.mock('../../api/directory', () => ({ directoryApi: { listUsers: vi.fn(), listDepartments: vi.fn(), listSites: vi.fn() } }))

const user: DirectoryUser = {
  id: 'user-1', username: 'enduser1', displayName: 'End User One', email: 'enduser1@example.test',
  role: 'EndUser', siteId: 'site-1', siteName: 'Head Office', departmentId: 'dept-1', departmentName: 'Finance',
}

const laptop: Ci = {
  id: 'ci-1', type: 'Hardware', name: 'LT-4417', assetTag: 'AT-4417', serialNumber: null, description: null,
  isActive: true, lifecycleState: 'Deployed',
  ownership: { ownerUserId: user.id, ownerName: user.displayName, departmentId: 'dept-1', departmentName: 'Finance', siteId: 'site-1', siteName: 'Head Office', assignedAt: '2026-08-01T00:00:00Z' },
  attributes: { manufacturer: 'Dell', model: 'Latitude' }, customFields: [],
  createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z',
}

const raised: Ticket = {
  id: 'ticket-1', number: 'INC-000007', title: 'Laptop will not charge', description: 'Dead', type: 'Incident',
  urgency: 'High', impact: 'Medium', priority: 'High', status: 'New', requesterId: 'enduser1',
  requesterName: 'End User One', queueId: null, queueName: null, assignedTechnicianId: null,
  createdAt: '2026-08-06T00:00:00Z', updatedAt: '2026-08-06T00:00:00Z', categoryId: null, categoryName: null, customFields: [],
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={[`/people/${user.id}`]}><QueryClientProvider client={client}>
    <Routes><Route path="/people/:userId" element={<UserDetailPage />} /></Routes>
  </QueryClientProvider></MemoryRouter>)
}

describe('UserDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(directoryApi.listUsers).mockResolvedValue([user])
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [laptop], total: 1, page: 1, pageSize: 50 })
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [raised], total: 1, page: 1, pageSize: 200 })
  })

  it('shows both worlds: the assets the person holds and the tickets they are in', async () => {
    renderPage()

    expect(await screen.findByRole('heading', { name: 'End User One' })).toBeInTheDocument()
    const assets = (await screen.findByRole('heading', { name: 'Assets' })).closest('section')!
    expect(within(assets).getByRole('link', { name: 'LT-4417' })).toHaveAttribute('href', '/assets/ci-1')
    const tickets = (await screen.findByRole('heading', { name: 'Tickets raised' })).closest('section')!
    expect(within(tickets).getByRole('link', { name: 'Laptop will not charge' })).toHaveAttribute('href', '/tickets/ticket-1')
  })

  /**
   * The keys differ on purpose: assets are owned by the directory's user id, while a ticket records
   * the identity the helpdesk saw — for seeded data, the username.
   */
  it('queries assets by directory id and tickets by username', async () => {
    renderPage()

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith({ ownerUserId: 'user-1', pageSize: 50 }))
    await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith({ requesterId: 'enduser1' }))
    expect(helpdeskApi.listTickets).toHaveBeenCalledWith({ assignedTechnicianId: 'enduser1' })
  })

  it('explains an id that is in no directory rather than rendering an empty page', async () => {
    vi.mocked(directoryApi.listUsers).mockResolvedValue([])
    renderPage()

    expect(await screen.findByText('Person could not be loaded')).toBeInTheDocument()
    expect(assetsApi.listCis).toHaveBeenCalled()
    expect(helpdeskApi.listTickets).not.toHaveBeenCalled()
  })
})
