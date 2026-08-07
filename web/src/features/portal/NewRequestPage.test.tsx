import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi } from '../../api/helpdesk'
import { NewRequestPage } from './NewRequestPage'
import { portalRequest } from './testRequest'

const { navigate } = vi.hoisted(() => ({ navigate: vi.fn() }))
vi.mock('react-router-dom', async (original) => {
  const actual = await original<typeof import('react-router-dom')>()
  return { ...actual, useNavigate: () => navigate }
})
vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listQueues: vi.fn(), createTicket: vi.fn() } }
})

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><NewRequestPage /></QueryClientProvider></MemoryRouter>)
}

describe('NewRequestPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(helpdeskApi.listQueues).mockResolvedValue([
      { id: 'queue-9', name: 'Network', teamId: 'team-9' },
      { id: 'queue-1', name: 'Service Desk', teamId: 'team-1' },
    ])
  })

  it('submits a request to the service desk queue and opens it', async () => {
    vi.mocked(helpdeskApi.createTicket).mockResolvedValue(portalRequest)
    renderPage()
    await screen.findByRole('radio', { name: /Something is broken/ })

    await userEvent.click(screen.getByRole('radio', { name: /I need something new/ }))
    await userEvent.type(screen.getByRole('textbox', { name: /Short summary/ }), 'Need Figma access')
    await userEvent.type(screen.getByRole('textbox', { name: /What is happening/ }), 'The design team asked me to join.')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: /How urgent/ }), 'Low')
    await userEvent.click(screen.getByRole('button', { name: 'Submit request' }))

    await waitFor(() => expect(helpdeskApi.createTicket).toHaveBeenCalledWith({
      title: 'Need Figma access',
      description: 'The design team asked me to join.',
      type: 'ServiceRequest',
      urgency: 'Low',
      impact: 'Medium',
      requesterId: null,
      queueId: 'queue-1',
    }))
    expect(navigate).toHaveBeenCalledWith('/portal/requests/ticket-1')
  })

  it('blocks an empty submission at the edge without calling the API', async () => {
    renderPage()
    await screen.findByRole('radio', { name: /Something is broken/ })

    await userEvent.click(screen.getByRole('button', { name: 'Submit request' }))

    expect(await screen.findByText('Enter at least 3 characters.')).toBeInTheDocument()
    expect(screen.getByText('Tell us what is happening.')).toBeInTheDocument()
    expect(helpdeskApi.createTicket).not.toHaveBeenCalled()
  })

  it('surfaces a server rejection without leaving the form', async () => {
    vi.mocked(helpdeskApi.createTicket).mockRejectedValue(new Error('Queue not found.'))
    renderPage()
    await screen.findByRole('radio', { name: /Something is broken/ })

    await userEvent.type(screen.getByRole('textbox', { name: /Short summary/ }), 'Laptop will not boot')
    await userEvent.type(screen.getByRole('textbox', { name: /What is happening/ }), 'Black screen since this morning.')
    await userEvent.click(screen.getByRole('button', { name: 'Submit request' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Queue not found.')
    expect(navigate).not.toHaveBeenCalled()
  })
})
