import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi, type Comment } from '../../api/helpdesk'
import { RequestDetailPage } from './RequestDetailPage'
import { portalRequest } from './testRequest'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, getTicket: vi.fn(), getComments: vi.fn(), addComment: vi.fn(), transition: vi.fn() } }
})

const publicReply: Comment = { id: 'comment-1', ticketId: 'ticket-1', body: 'We have reset your VPN profile.', isInternal: false, authorId: 'technician1', authorName: 'Tech One', createdAt: '2026-08-07T02:00:00Z' }

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/portal/requests/ticket-1']}><QueryClientProvider client={client}>
    <Routes><Route path="/portal/requests/:id" element={<RequestDetailPage />} /></Routes>
  </QueryClientProvider></MemoryRouter>)
}

describe('RequestDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(helpdeskApi.getTicket).mockResolvedValue(portalRequest)
    vi.mocked(helpdeskApi.getComments).mockResolvedValue([publicReply])
  })

  it('shows only the replies the API returns to the requester', async () => {
    renderPage()

    expect(await screen.findByText('We have reset your VPN profile.')).toBeInTheDocument()
    expect(screen.getByText('Tech One')).toBeInTheDocument()
    expect(screen.queryByText(/internal/i)).not.toBeInTheDocument()
  })

  it('always posts requester replies as public comments', async () => {
    vi.mocked(helpdeskApi.addComment).mockResolvedValue({ ...publicReply, id: 'comment-2', body: 'Still failing.', authorName: 'Requester One' })
    renderPage()

    await userEvent.type(await screen.findByRole('textbox', { name: 'Add a reply' }), 'Still failing.')
    await userEvent.click(screen.getByRole('button', { name: 'Send reply' }))

    await waitFor(() => expect(helpdeskApi.addComment).toHaveBeenCalledWith('ticket-1', 'Still failing.', false))
  })

  it('confirms before closing a resolved request', async () => {
    vi.mocked(helpdeskApi.transition).mockResolvedValue({ ...portalRequest, status: 'Closed' })
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Confirm and close' }))
    expect(helpdeskApi.transition).not.toHaveBeenCalled()

    const dialog = screen.getByRole('dialog', { name: 'Close this request?' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Yes, close it' }))

    await waitFor(() => expect(helpdeskApi.transition).toHaveBeenCalledWith('ticket-1', 'Closed', null))
  })

  it('keeps the request open and reports the error when closing is refused', async () => {
    vi.mocked(helpdeskApi.transition).mockRejectedValue(new Error('Requesters may only close a resolved ticket.'))
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Confirm and close' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Yes, close it' }))

    expect(await within(screen.getByRole('dialog')).findByRole('alert'))
      .toHaveTextContent('Requesters may only close a resolved ticket.')
  })

  it('does not offer a close action while the request is still open', async () => {
    vi.mocked(helpdeskApi.getTicket).mockResolvedValue({ ...portalRequest, status: 'InProgress' })
    renderPage()

    expect(await screen.findByText('VPN unavailable')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirm and close' })).not.toBeInTheDocument()
  })
})
