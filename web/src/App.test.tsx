import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { App } from './App'
import { helpdeskApi } from './api/helpdesk'
import type { AppRole } from './auth/auth'

const { authState } = vi.hoisted(() => ({ authState: { roles: [] as AppRole[] } }))
vi.mock('./auth/AuthProvider', () => ({
  useAuth: () => ({ user: { profile: { name: 'End User', email: 'enduser@example.test' } }, roles: authState.roles, isLoading: false, signIn: vi.fn(), signOut: vi.fn() }),
}))
vi.mock('./api/helpdesk', async (original) => {
  const actual = await original<typeof import('./api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listTickets: vi.fn(), listQueues: vi.fn() } }
})

function renderAt(path: string, roles: AppRole[]) {
  authState.roles = roles
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={[path]}><QueryClientProvider client={client}><App /></QueryClientProvider></MemoryRouter>)
}

describe('App routing', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 })
    vi.mocked(helpdeskApi.listQueues).mockResolvedValue([])
  })

  it('sends an EndUser from the root route to the self-service portal', async () => {
    renderAt('/', ['EndUser'])

    expect(await screen.findByRole('heading', { name: 'My requests' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'Primary navigation' })).not.toBeInTheDocument()
  })

  it('refuses an EndUser access to the agent ticket queue', async () => {
    renderAt('/tickets', ['EndUser'])

    expect(await screen.findByText('You do not have access')).toBeInTheDocument()
  })

  it('refuses an agent access to the requester portal', async () => {
    renderAt('/portal', ['Technician'])

    expect(await screen.findByText('You do not have access')).toBeInTheDocument()
  })

  it('keeps an agent on the admin shell at the root route', async () => {
    renderAt('/', ['Technician'])

    expect(await screen.findByRole('navigation', { name: 'Primary navigation' })).toBeInTheDocument()
  })
})
