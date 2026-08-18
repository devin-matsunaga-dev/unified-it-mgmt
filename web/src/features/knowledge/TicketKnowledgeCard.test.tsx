import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, test, vi } from 'vitest'
import { knowledgeApi, type KbSuggestion, type TicketKbArticle } from '../../api/knowledge'
import { TicketKnowledgeCard } from './TicketKnowledgeCard'

vi.mock('../../api/knowledge', async (original) => {
  const actual = await original<typeof import('../../api/knowledge')>()
  return {
    ...actual,
    knowledgeApi: {
      listForTicket: vi.fn(),
      suggest: vi.fn(),
      attachToTicket: vi.fn(),
      detachFromTicket: vi.fn(),
    },
  }
})

const suggestion: KbSuggestion = {
  id: 'kb-1',
  number: 'KB-000001',
  title: 'Wi-Fi drops for a minute at a time',
  summary: 'Short repeated drop-outs on the office Wi-Fi.',
  categoryName: 'Network',
  publishedAt: '2026-08-10T09:00:00Z',
  rank: 0.42,
}

const attached: TicketKbArticle = {
  articleId: 'kb-2',
  number: 'KB-000002',
  title: 'Connecting to the VPN from home',
  summary: 'How to sign in.',
  status: 'Published',
  linkedById: 'technician1',
  linkedByName: 'Technician One',
  linkedAt: '2026-08-16T09:00:00Z',
}

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <TicketKnowledgeCard
          ticketId="tkt-1"
          subject="Wi-Fi keeps dropping on the second floor"
          body="It goes for a minute and comes back."
        />
      </MemoryRouter>
    </QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

/** The WP's third verification step from the agent's side: the article that answered it gets attached. */
test('a suggested article can be attached to the ticket', async () => {
  vi.mocked(knowledgeApi.listForTicket).mockResolvedValue([])
  vi.mocked(knowledgeApi.suggest).mockResolvedValue([suggestion])
  vi.mocked(knowledgeApi.attachToTicket).mockResolvedValue({ ...attached, articleId: 'kb-1', number: 'KB-000001' })

  renderCard()

  expect(await screen.findByText('Wi-Fi drops for a minute at a time')).toBeInTheDocument()
  await userEvent.click(screen.getByRole('button', { name: 'Attach' }))

  await waitFor(() => expect(knowledgeApi.attachToTicket).toHaveBeenCalledWith('tkt-1', 'kb-1'))
})

/** An attached article is a link to the article and a record of who attached it. */
test('attached articles are listed and can be detached', async () => {
  vi.mocked(knowledgeApi.listForTicket).mockResolvedValue([attached])
  vi.mocked(knowledgeApi.suggest).mockResolvedValue([])
  vi.mocked(knowledgeApi.detachFromTicket).mockResolvedValue(undefined)

  renderCard()

  const link = await screen.findByRole('link', { name: 'Connecting to the VPN from home' })
  expect(link).toHaveAttribute('href', '/knowledge/kb-2')
  expect(screen.getByText(/attached by Technician One/)).toBeInTheDocument()

  await userEvent.click(screen.getByRole('button', { name: 'Detach KB-000002' }))

  await waitFor(() => expect(knowledgeApi.detachFromTicket).toHaveBeenCalledWith('tkt-1', 'kb-2'))
})

/** An article already on the ticket is not offered again as a suggestion. */
test('an already attached article is not suggested a second time', async () => {
  vi.mocked(knowledgeApi.listForTicket).mockResolvedValue([{ ...attached, articleId: 'kb-1', number: 'KB-000001', title: suggestion.title }])
  vi.mocked(knowledgeApi.suggest).mockResolvedValue([suggestion])

  renderCard()

  await waitFor(() => expect(knowledgeApi.suggest).toHaveBeenCalled())
  expect(screen.queryByRole('button', { name: 'Attach' })).not.toBeInTheDocument()
})

/**
 * Most tickets have nothing attached and nothing to suggest, and a card that says so on every one of them
 * is a card people stop reading — the same call `RelatedProblemsCard` makes.
 */
test('a ticket with nothing attached and nothing suggested renders nothing at all', async () => {
  vi.mocked(knowledgeApi.listForTicket).mockResolvedValue([])
  vi.mocked(knowledgeApi.suggest).mockResolvedValue([])

  const { container } = renderCard()

  await waitFor(() => expect(knowledgeApi.suggest).toHaveBeenCalled())
  expect(container).toBeEmptyDOMElement()
})
