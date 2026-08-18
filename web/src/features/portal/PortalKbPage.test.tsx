import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, test, vi } from 'vitest'
import { knowledgeApi, type KbArticle } from '../../api/knowledge'
import { PortalKbPage } from './PortalKbPage'

vi.mock('../../api/knowledge', async (original) => {
  const actual = await original<typeof import('../../api/knowledge')>()
  return { ...actual, knowledgeApi: { ...actual.knowledgeApi, list: vi.fn() } }
})

const published: KbArticle = {
  id: 'kb-1',
  number: 'KB-000001',
  title: 'Connecting to the VPN from home',
  summary: 'How to sign in, and what to try when it will not connect.',
  body: 'The steps.',
  keywords: 'vpn',
  status: 'Published',
  categoryId: null,
  categoryName: 'Network',
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
  nextStatuses: [],
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><PortalKbPage /></MemoryRouter>
    </QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

/**
 * The portal asks for articles without asking for a state, because the state is not its decision: the
 * server narrows an end user to published articles inside the query. A page that sent `status=Published`
 * would look identical and be wrong — it would imply the filter is what protects a draft.
 */
test('the portal asks for articles without naming a status', async () => {
  vi.mocked(knowledgeApi.list).mockResolvedValue({ items: [published], total: 1, page: 1, pageSize: 25 })

  renderPage()

  expect(await screen.findByRole('link', { name: /Connecting to the VPN from home/ }))
    .toHaveAttribute('href', '/portal/kb/kb-1')
  const call = vi.mocked(knowledgeApi.list).mock.calls[0][0]
  expect(call).not.toHaveProperty('statuses')
})

test('searching narrows the same read', async () => {
  vi.mocked(knowledgeApi.list).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })

  renderPage()
  await userEvent.type(await screen.findByPlaceholderText('What do you need help with?'), 'vpn')

  await waitFor(() => expect(knowledgeApi.list).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'vpn' })))
  expect(await screen.findByText('Nothing here matches that')).toBeInTheDocument()
})

/** Never a bare "No data" (DESIGN §6) — and an empty help centre still offers the way forward. */
test('an empty knowledge base still points at raising a request', async () => {
  vi.mocked(knowledgeApi.list).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })

  renderPage()

  expect(await screen.findByText('No articles yet')).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'New request' })).toHaveAttribute('href', '/portal/new')
})
