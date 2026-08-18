import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, expect, test, vi } from 'vitest'
import { knowledgeApi, type KbArticle } from '../../api/knowledge'
import { KbArticlePage } from './KbArticlePage'

vi.mock('../../api/knowledge', async (original) => {
  const actual = await original<typeof import('../../api/knowledge')>()
  return {
    ...actual,
    knowledgeApi: {
      get: vi.fn(),
      update: vi.fn(),
      transition: vi.fn(),
      restore: vi.fn(),
      remove: vi.fn(),
    },
  }
})

const article: KbArticle = {
  id: 'kb-1',
  number: 'KB-000001',
  title: 'Connecting to the VPN from home',
  summary: 'How to sign in, and what to try when it will not connect.',
  body: '## Connecting\n\n1. Open the client.\n2. Press connect.',
  keywords: 'vpn, remote access',
  status: 'Draft',
  categoryId: null,
  categoryName: null,
  problemId: null,
  problemNumber: null,
  version: 2,
  authorId: 'technician1',
  authorName: 'Technician One',
  publishedById: null,
  publishedByName: null,
  publishedAt: null,
  archivedAt: null,
  createdAt: '2026-08-10T09:00:00Z',
  updatedAt: '2026-08-16T09:00:00Z',
  linkedTicketCount: 0,
  nextStatuses: ['Published', 'Archived'],
  revisions: [{
    version: 1,
    title: 'VPN',
    summary: 'Old summary.',
    body: 'The first attempt at writing this down.',
    keywords: null,
    authorId: 'technician1',
    authorName: 'Technician One',
    createdAt: '2026-08-15T09:00:00Z',
  }],
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/knowledge/kb-1']}>
        <Routes><Route path="/knowledge/:id" element={<KbArticlePage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

/**
 * The workflow buttons come off the record, not from a copy of the workflow kept here — WP-5.8's call,
 * because a browser-side table eventually withholds a button nobody knew to ask for.
 */
test('the transition buttons are exactly what the server said the next statuses are', async () => {
  vi.mocked(knowledgeApi.get).mockResolvedValue(article)
  vi.mocked(knowledgeApi.transition).mockResolvedValue({ ...article, status: 'Published', nextStatuses: ['Draft', 'Archived'] })

  renderPage()

  expect(await screen.findByRole('button', { name: 'Publish' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Archive' })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Back to draft' })).not.toBeInTheDocument()

  await userEvent.click(screen.getByRole('button', { name: 'Publish' }))

  await waitFor(() => expect(knowledgeApi.transition).toHaveBeenCalledWith('kb-1', 'Published'))
})

/** The body is rendered as its Markdown shapes rather than as one blob of preformatted text. */
test('the body renders its headings and numbered steps', async () => {
  vi.mocked(knowledgeApi.get).mockResolvedValue(article)

  renderPage()

  expect(await screen.findByRole('heading', { name: 'Connecting' })).toBeInTheDocument()
  expect(screen.getByText('Open the client.')).toBeInTheDocument()
})

/**
 * A history is only worth keeping if it can be read, so each version carries its prose — and restoring
 * moves forward as a new version rather than rewinding the count.
 */
test('an earlier version can be read and restored', async () => {
  vi.mocked(knowledgeApi.get).mockResolvedValue(article)
  vi.mocked(knowledgeApi.restore).mockResolvedValue({ ...article, version: 3 })

  renderPage()

  await userEvent.click(await screen.findByRole('button', { name: /Version history/ }))
  expect(screen.getByText('The first attempt at writing this down.')).toBeInTheDocument()

  await userEvent.click(screen.getByRole('button', { name: 'Restore' }))

  await waitFor(() => expect(knowledgeApi.restore).toHaveBeenCalledWith('kb-1', 1))
})

/**
 * Deleting is refused for an article a ticket was answered with, and the screen says so rather than
 * offering a button that fails — those attachments are the record of what somebody was told.
 */
test('an article attached to a ticket cannot be deleted and explains why', async () => {
  vi.mocked(knowledgeApi.get).mockResolvedValue({ ...article, linkedTicketCount: 3 })

  renderPage()

  expect(await screen.findByRole('button', { name: 'Delete' })).toBeDisabled()
  expect(screen.getByText(/3 tickets have been answered with this/)).toBeInTheDocument()
})

/** Editing writes a version, and the form sends what is on screen rather than what was loaded. */
test('editing saves a new version', async () => {
  vi.mocked(knowledgeApi.get).mockResolvedValue(article)
  vi.mocked(knowledgeApi.update).mockResolvedValue({ ...article, version: 3 })

  renderPage()

  await userEvent.click(await screen.findByRole('button', { name: 'Edit' }))
  const title = screen.getByLabelText('Title')
  await userEvent.clear(title)
  await userEvent.type(title, 'Connecting to the VPN')
  await userEvent.click(screen.getByRole('button', { name: 'Save version' }))

  await waitFor(() => expect(knowledgeApi.update).toHaveBeenCalledWith('kb-1', expect.objectContaining({
    title: 'Connecting to the VPN',
  })))
})
