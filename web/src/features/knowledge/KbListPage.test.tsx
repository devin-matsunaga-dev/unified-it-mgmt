import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, test, vi } from 'vitest'
import { knowledgeApi, type KbArticle } from '../../api/knowledge'
import { KbListPage } from './KbListPage'

vi.mock('../../api/knowledge', async (original) => {
  const actual = await original<typeof import('../../api/knowledge')>()
  return { ...actual, knowledgeApi: { ...actual.knowledgeApi, list: vi.fn(), create: vi.fn() } }
})
vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listCategories: vi.fn().mockResolvedValue([]) } }
})

const base: KbArticle = {
  id: 'kb-1',
  number: 'KB-000001',
  title: 'Connecting to the VPN from home',
  summary: 'How to sign in.',
  body: 'The steps.',
  keywords: 'vpn',
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
  linkedTicketCount: 2,
  nextStatuses: ['Draft', 'Archived'],
}

const draft: KbArticle = { ...base, id: 'kb-2', number: 'KB-000002', title: 'Requesting a new laptop', status: 'Draft', linkedTicketCount: 0 }

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><KbListPage /></MemoryRouter>
    </QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

/**
 * The service desk sees every state, because the person who has to finish a draft is the one who needs to
 * find it — and the counts say how much of it the portal can actually reach.
 */
test('the list shows drafts beside published articles and counts both', async () => {
  vi.mocked(knowledgeApi.list).mockResolvedValue({ items: [base, draft], total: 2, page: 1, pageSize: 100 })

  renderPage()

  expect(await screen.findByRole('link', { name: 'Connecting to the VPN from home' })).toHaveAttribute('href', '/knowledge/kb-1')
  expect(screen.getByRole('link', { name: 'Requesting a new laptop' })).toBeInTheDocument()
  expect(screen.getByText('Published here').parentElement).toHaveTextContent('1')
  expect(screen.getByText('Still drafts').parentElement).toHaveTextContent('1')
})

/** The search box and the status filter are one query, so a filtered read is one request and not two. */
test('searching and filtering by status go to the server together', async () => {
  vi.mocked(knowledgeApi.list).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 100 })

  renderPage()
  await userEvent.type(await screen.findByPlaceholderText(/Search titles/), 'vpn')
  await userEvent.selectOptions(screen.getByLabelText('Filter by status'), 'Draft')

  await waitFor(() => expect(knowledgeApi.list).toHaveBeenLastCalledWith(expect.objectContaining({
    search: 'vpn',
    statuses: ['Draft'],
  })))
})

/** Never a bare "No data" (DESIGN §6), and the two empty states are different facts. */
test('an empty knowledge base invites the first article; a filtered one does not', async () => {
  vi.mocked(knowledgeApi.list).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 100 })

  renderPage()

  expect(await screen.findByText('Nothing has been written down yet')).toBeInTheDocument()
  expect(screen.getAllByRole('button', { name: /New article/ })).toHaveLength(2)

  await userEvent.type(screen.getByPlaceholderText(/Search titles/), 'vpn')

  expect(await screen.findByText('Nothing matches that')).toBeInTheDocument()
})

/** A new article is always a draft, which is what the dialog says and what it sends. */
test('creating an article sends a draft and nothing about its state', async () => {
  vi.mocked(knowledgeApi.list).mockResolvedValue({ items: [base], total: 1, page: 1, pageSize: 100 })
  vi.mocked(knowledgeApi.create).mockResolvedValue(draft)

  renderPage()
  await userEvent.click(await screen.findByRole('button', { name: /New article/ }))
  await userEvent.type(screen.getByLabelText('Title'), 'Requesting a new laptop')
  await userEvent.type(screen.getByLabelText('Summary'), 'Who can ask, and what happens next.')
  await userEvent.type(screen.getByLabelText('Body'), 'Anyone whose machine is over four years old.')
  await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))

  await waitFor(() => expect(knowledgeApi.create).toHaveBeenCalledWith({
    title: 'Requesting a new laptop',
    summary: 'Who can ask, and what happens next.',
    body: 'Anyone whose machine is over four years old.',
    keywords: null,
    categoryId: null,
  }))
})
