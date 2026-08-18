import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { helpdeskApi } from '../../api/helpdesk'
import { knowledgeApi, type KbArticle, type KbSuggestion } from '../../api/knowledge'
import { NewRequestPage } from './NewRequestPage'
import { portalRequest } from './testRequest'

const { navigate } = vi.hoisted(() => ({ navigate: vi.fn() }))
vi.mock('react-router-dom', async (original) => {
  const actual = await original<typeof import('react-router-dom')>()
  return { ...actual, useNavigate: () => navigate }
})
vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listQueues: vi.fn(), listCategories: vi.fn(), createTicket: vi.fn() } }
})
vi.mock('../../api/knowledge', async (original) => {
  const actual = await original<typeof import('../../api/knowledge')>()
  return { ...actual, knowledgeApi: { ...actual.knowledgeApi, suggest: vi.fn(), get: vi.fn() } }
})

const suggestion: KbSuggestion = {
  id: 'kb-1',
  number: 'KB-000001',
  title: 'Connecting to the VPN from home',
  summary: 'How to sign in to the VPN, and what to try when it will not connect.',
  categoryName: null,
  publishedAt: '2026-08-10T09:00:00Z',
  rank: 0.5,
}

const article = {
  id: 'kb-1',
  number: 'KB-000001',
  title: suggestion.title,
  summary: suggestion.summary,
  body: '## If it will not connect\n\n- Sign out of the client and back in.',
  keywords: null,
  status: 'Published',
  categoryId: null,
  categoryName: null,
  problemId: null,
  problemNumber: null,
  version: 1,
  authorId: 'technician1',
  authorName: 'Technician One',
  publishedById: 'technician1',
  publishedByName: 'Technician One',
  publishedAt: '2026-08-10T09:00:00Z',
  archivedAt: null,
  createdAt: '2026-08-09T09:00:00Z',
  updatedAt: '2026-08-10T09:00:00Z',
  linkedTicketCount: 0,
  nextStatuses: ['Draft', 'Archived'],
} satisfies KbArticle

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><NewRequestPage /></QueryClientProvider></MemoryRouter>)
}

async function fillIn() {
  await userEvent.type(await screen.findByRole('textbox', { name: /Short summary/ }), 'VPN will not connect from home')
  await userEvent.type(screen.getByRole('textbox', { name: /What is happening/ }), 'It times out every time I press connect.')
}

describe('the portal deflection prompt', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(helpdeskApi.listQueues).mockResolvedValue([{ id: 'queue-1', name: 'Service Desk', teamId: 'team-1' }])
    vi.mocked(helpdeskApi.listCategories).mockResolvedValue([])
    vi.mocked(knowledgeApi.suggest).mockResolvedValue([suggestion])
    vi.mocked(knowledgeApi.get).mockResolvedValue(article)
  })

  /**
   * The prompt is offered from what has been typed, before anything is submitted — which is the whole
   * point of deflection: the answer arrives while the question is still being written.
   */
  it('offers a matching article while the request is being typed', async () => {
    renderPage()
    await fillIn()

    expect(await screen.findByText('This may already be answered')).toBeInTheDocument()
    expect(screen.getByText(suggestion.title)).toBeInTheDocument()
    await waitFor(() => expect(knowledgeApi.suggest).toHaveBeenCalledWith(expect.objectContaining({
      subject: 'VPN will not connect from home',
    })))
  })

  /**
   * The first press of Submit shows the prompt and sends nothing; the second sends it. A prompt nobody can
   * get past is a portal people stop using, and a prompt that never interrupts is one nobody reads.
   */
  it('holds the first submission, then submits on the second press', async () => {
    vi.mocked(helpdeskApi.createTicket).mockResolvedValue(portalRequest)
    renderPage()
    await fillIn()
    await screen.findByText('This may already be answered')

    await userEvent.click(screen.getByRole('button', { name: /Submit request/ }))

    expect(await screen.findByText('Before you send this — one of these may answer it')).toBeInTheDocument()
    expect(helpdeskApi.createTicket).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: /Submit anyway/ }))

    await waitFor(() => expect(helpdeskApi.createTicket).toHaveBeenCalled())
  })

  /** An article opens in place: a half-typed request lost to a link is worse than no prompt at all. */
  it('expands the article inline rather than navigating away', async () => {
    renderPage()
    await fillIn()

    await userEvent.click(await screen.findByRole('button', { name: /Connecting to the VPN from home/ }))

    expect(await screen.findByText('Sign out of the client and back in.')).toBeInTheDocument()
    expect(navigate).not.toHaveBeenCalled()
    // And the request keeps everything that was typed.
    expect(screen.getByRole('textbox', { name: /Short summary/ })).toHaveValue('VPN will not connect from home')
  })

  /** With nothing to suggest there is no prompt and no extra press — the form behaves exactly as it did. */
  it('submits on the first press when the knowledge base has nothing to offer', async () => {
    vi.mocked(knowledgeApi.suggest).mockResolvedValue([])
    vi.mocked(helpdeskApi.createTicket).mockResolvedValue(portalRequest)
    renderPage()
    await fillIn()
    await waitFor(() => expect(knowledgeApi.suggest).toHaveBeenCalled())

    await userEvent.click(screen.getByRole('button', { name: /Submit request/ }))

    await waitFor(() => expect(helpdeskApi.createTicket).toHaveBeenCalled())
    expect(screen.queryByText('This may already be answered')).not.toBeInTheDocument()
  })
})
