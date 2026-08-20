import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci } from '../../api/assets'
import { helpdeskApi, type Ticket } from '../../api/helpdesk'
import { FieldTicketPage } from './FieldTicketPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, getCi: vi.fn() } }
})
vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, createTicket: vi.fn() } }
})

const laptop = { id: 'ci-1', name: 'Reception laptop' } as Ci

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/ci/ci-1/ticket']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/ci/:id/ticket" element={<FieldTicketPage />} />
        <Route path="/field/ci/:id" element={<h1>Field asset page</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

async function fillIn() {
  await userEvent.type(screen.getByLabelText('What is wrong?'), 'Will not power on')
  await userEvent.type(screen.getByLabelText('What did you see?'), 'No lights, no fan.')
}

describe('FieldTicketPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop)
  })

  it('links the CI in the create call rather than a second request', async () => {
    vi.mocked(helpdeskApi.createTicket).mockResolvedValue({ id: 't-1', number: 'INC-1043' } as Ticket)

    renderPage()
    await fillIn()
    await userEvent.click(screen.getByRole('button', { name: /Raise ticket/ }))

    await waitFor(() => expect(helpdeskApi.createTicket).toHaveBeenCalledTimes(1))
    expect(vi.mocked(helpdeskApi.createTicket).mock.calls[0][0]).toMatchObject({
      title: 'Will not power on',
      description: 'No lights, no fan.',
      urgency: 'Medium',
      ciIds: ['ci-1'],
    })
  })

  it('carries the urgency the technician chose', async () => {
    vi.mocked(helpdeskApi.createTicket).mockResolvedValue({ id: 't-1', number: 'INC-1043' } as Ticket)

    renderPage()
    await fillIn()
    await userEvent.click(screen.getByRole('button', { name: 'High' }))
    await userEvent.click(screen.getByRole('button', { name: /Raise ticket/ }))

    await waitFor(() => expect(vi.mocked(helpdeskApi.createTicket).mock.calls[0][0]).toMatchObject({ urgency: 'High' }))
  })

  it('will not submit an empty report', async () => {
    renderPage()

    expect(screen.getByRole('button', { name: /Raise ticket/ })).toBeDisabled()
    await userEvent.type(screen.getByLabelText('What is wrong?'), 'Will not power on')
    expect(screen.getByRole('button', { name: /Raise ticket/ })).toBeDisabled()
  })

  it('stays on the form when the create fails, so the typing is not lost', async () => {
    vi.mocked(helpdeskApi.createTicket).mockRejectedValue(new Error('Network request failed'))

    renderPage()
    await fillIn()
    await userEvent.click(screen.getByRole('button', { name: /Raise ticket/ }))

    await waitFor(() => expect(helpdeskApi.createTicket).toHaveBeenCalled())
    expect(screen.queryByRole('heading', { name: 'Field asset page' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('What is wrong?')).toHaveValue('Will not power on')
  })
})
