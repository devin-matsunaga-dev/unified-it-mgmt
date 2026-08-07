import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi } from '../../api/helpdesk'
import { MyRequestsPage } from './MyRequestsPage'
import { portalRequest } from './testRequest'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listTickets: vi.fn() } }
})

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><MyRequestsPage /></QueryClientProvider></MemoryRouter>)
}

describe('MyRequestsPage', () => {
  beforeEach(() => vi.clearAllMocks())

  it('lists the signed-in requester tickets with their status', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [portalRequest], total: 1, page: 1, pageSize: 200 })
    renderPage()

    expect(await screen.findByRole('link', { name: /VPN unavailable/ })).toHaveAttribute('href', '/portal/requests/ticket-1')
    expect(screen.getByText('Resolved')).toBeInTheDocument()
  })

  it('invites a first request when the requester has none', async () => {
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 })
    renderPage()

    expect(await screen.findByText('You have not submitted any requests yet')).toBeInTheDocument()
  })

  it('offers a retry when the request list fails to load', async () => {
    vi.mocked(helpdeskApi.listTickets).mockRejectedValue(new Error('Service unavailable'))
    renderPage()

    expect(await screen.findByRole('alert')).toHaveTextContent('Your requests could not be loaded')
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [portalRequest], total: 1, page: 1, pageSize: 200 })
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByText('VPN unavailable')).toBeInTheDocument()
  })
})
