import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi, type TicketCategory } from '../../api/helpdesk'
import { NewRequestPage } from './NewRequestPage'

vi.mock('react-router-dom', async (original) => {
  const actual = await original<typeof import('react-router-dom')>()
  return { ...actual, useNavigate: () => vi.fn() }
})
vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listQueues: vi.fn(), listCategories: vi.fn(), createTicket: vi.fn() } }
})

// Captured verbatim from GET /api/ticket-categories in the integration test.
const apiPayload = JSON.parse('[{"id":"01980000-0000-7000-8000-000000000501","name":"Hardware","parentId":null,"isActive":true,"sortOrder":1,"fields":[],"children":[{"id":"019fda2f-8628-7613-97f3-9434aaba10b8","name":"Laptop issue","parentId":"01980000-0000-7000-8000-000000000501","isActive":true,"sortOrder":0,"fields":[{"id":"019fda2f-8661-7904-a761-45cfa14709b7","categoryId":"019fda2f-8628-7613-97f3-9434aaba10b8","key":"asset_tag","label":"Asset tag","type":"Text","isRequired":true,"options":[],"sortOrder":0}],"children":[]},{"id":"01980000-0000-7000-8000-000000000511","name":"Laptop or desktop","parentId":"01980000-0000-7000-8000-000000000501","isActive":true,"sortOrder":1,"fields":[],"children":[]}]}]') as TicketCategory[]

describe('NewRequestPage against the real API payload', () => {
  it('reveals the required custom field of the selected child category', async () => {
    vi.mocked(helpdeskApi.listQueues).mockResolvedValue([{ id: 'queue-1', name: 'Service Desk', teamId: 'team-1' }])
    vi.mocked(helpdeskApi.listCategories).mockResolvedValue(apiPayload)
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    render(<MemoryRouter><QueryClientProvider client={client}><NewRequestPage /></QueryClientProvider></MemoryRouter>)

    await userEvent.selectOptions(await screen.findByLabelText('Category'), '019fda2f-8628-7613-97f3-9434aaba10b8')

    expect(screen.getByLabelText(/Asset tag/)).toBeInTheDocument()
  })
})
