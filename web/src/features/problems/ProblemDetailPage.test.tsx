import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { helpdeskApi } from '../../api/helpdesk'
import { problemsApi, type Problem } from '../../api/problems'
import { ProblemDetailPage } from './ProblemDetailPage'

vi.mock('../../api/problems', async (original) => {
  const actual = await original<typeof import('../../api/problems')>()
  return {
    ...actual,
    problemsApi: {
      get: vi.fn(),
      update: vi.fn(),
      transition: vi.fn(),
      linkIncident: vi.fn(),
      unlinkIncident: vi.fn(),
    },
  }
})

vi.mock('../../api/helpdesk', () => ({ helpdeskApi: { listTickets: vi.fn() } }))

const investigating: Problem = {
  id: 'prb-1',
  number: 'PRB-000001',
  title: 'Second floor access point drops clients',
  description: 'Five incidents in a week.',
  status: 'Investigating',
  priority: 'High',
  isKnownError: false,
  subject: { scope: 'Ci', id: 'ci-1', name: 'HQ floor 2 access point', type: 'NetworkDevice' },
  rootCause: null,
  workaround: null,
  resolution: null,
  assignedTechnicianId: null,
  openedById: 'technician1',
  openedByName: 'Technician One',
  incidentCount: 2,
  createdAt: '2026-08-15T09:00:00Z',
  updatedAt: '2026-08-16T09:00:00Z',
  knownErrorAt: null,
  resolvedAt: null,
  closedAt: null,
  incidents: [
    {
      ticketId: 'tkt-1',
      number: 'INC-000001',
      title: 'Wi-Fi keeps dropping',
      status: 'InProgress',
      priority: 'High',
      createdAt: '2026-08-15T08:00:00Z',
      linkedById: 'technician1',
      linkedByName: 'Technician One',
      linkedAt: '2026-08-15T09:00:00Z',
    },
    {
      ticketId: 'tkt-2',
      number: 'INC-000002',
      title: 'Video calls cut out',
      status: 'New',
      priority: 'Medium',
      createdAt: '2026-08-16T08:00:00Z',
      linkedById: 'technician1',
      linkedByName: 'Technician One',
      linkedAt: '2026-08-16T09:00:00Z',
    },
  ],
}

const knownError: Problem = {
  ...investigating,
  status: 'KnownError',
  isKnownError: true,
  rootCause: 'A failing radio.',
  workaround: 'Associate to the floor 3 access point.',
  knownErrorAt: '2026-08-16T10:00:00Z',
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/problems/prb-1']}>
        <Routes><Route path="/problems/:id" element={<ProblemDetailPage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.mocked(problemsApi.get).mockResolvedValue(investigating)
  vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })
})

afterEach(() => vi.clearAllMocks())

/**
 * The entry condition, enforced in the browser as well as on the server — not as the guard, which is the
 * server's, but so that somebody is not told no by a button they were invited to press.
 */
test('becoming a known error is unavailable until both halves are recorded', async () => {
  renderPage()
  await screen.findByText('Second floor access point drops clients')

  expect(screen.getByRole('button', { name: 'Known error' })).toBeDisabled()

  await userEvent.type(screen.getByLabelText('Root cause'), 'A failing radio.')
  expect(screen.getByRole('button', { name: 'Known error' })).toBeDisabled()

  await userEvent.type(screen.getByLabelText('Workaround'), 'Associate to the floor 3 access point.')
  // Still disabled: the edit has to be saved first, because the server checks what is stored.
  expect(screen.getByRole('button', { name: 'Known error' })).toBeDisabled()
  expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
})

test('saving the cause and workaround sends both', async () => {
  vi.mocked(problemsApi.update).mockResolvedValue(knownError)
  renderPage()
  await screen.findByText('Second floor access point drops clients')

  await userEvent.type(screen.getByLabelText('Root cause'), 'A failing radio.')
  await userEvent.type(screen.getByLabelText('Workaround'), 'Associate to the floor 3 access point.')
  await userEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(problemsApi.update).toHaveBeenCalledWith('prb-1', expect.objectContaining({
    rootCause: 'A failing radio.',
    workaround: 'Associate to the floor 3 access point.',
    ciId: 'ci-1',
    categoryId: null,
  })))
})

test('a saved known error offers the transition and says since when it has been published', async () => {
  vi.mocked(problemsApi.get).mockResolvedValue(knownError)
  renderPage()
  await screen.findByText('Second floor access point drops clients')

  expect(screen.getByText(/Published as a known error since/)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Resolved' })).toBeDisabled()
})

test('resolving and closing stay unavailable until a resolution is written', async () => {
  vi.mocked(problemsApi.transition).mockResolvedValue({ problem: investigating, knowledgeDraft: null })
  renderPage()
  await screen.findByText('Second floor access point drops clients')

  expect(screen.getByRole('button', { name: 'Closed' })).toBeDisabled()

  await userEvent.type(screen.getByLabelText('Resolution'), 'Access point replaced under warranty.')

  await waitFor(() => expect(screen.getByRole('button', { name: 'Closed' })).toBeEnabled())
})

/** The WP's third verification step, in the browser: closing prompts for the article. */
test('closing a problem opens the knowledge article draft it was answered with', async () => {
  vi.mocked(problemsApi.transition).mockResolvedValue({
    problem: { ...investigating, status: 'Closed', closedAt: '2026-08-17T09:00:00Z' },
    knowledgeDraft: {
      problemId: 'prb-1',
      problemNumber: 'PRB-000001',
      title: 'Second floor access point drops clients',
      subjectName: 'HQ floor 2 access point',
      symptoms: [{ text: 'Wi-Fi keeps dropping', incidentCount: 3 }],
      rootCause: 'A failing radio.',
      workaround: 'Associate to the floor 3 access point.',
      resolution: 'Access point replaced under warranty.',
      incidentNumbers: ['INC-000001', 'INC-000002'],
    },
  })
  renderPage()
  await screen.findByText('Second floor access point drops clients')
  await userEvent.type(screen.getByLabelText('Resolution'), 'Access point replaced under warranty.')

  await userEvent.click(screen.getByRole('button', { name: 'Closed' }))

  const dialog = await screen.findByRole('dialog', { name: 'Knowledge article draft' })
  expect(dialog).toBeInTheDocument()
  expect(screen.getByText('reported 3×')).toBeInTheDocument()
  expect(screen.getByText('INC-000001, INC-000002')).toBeInTheDocument()
})

/** The draft's whole job is to show what is still missing, so a blank field says so. */
test('a draft field nobody filled in says the article will need it', async () => {
  vi.mocked(problemsApi.transition).mockResolvedValue({
    problem: { ...investigating, status: 'Closed' },
    knowledgeDraft: {
      problemId: 'prb-1',
      problemNumber: 'PRB-000001',
      title: 'Second floor access point drops clients',
      subjectName: null,
      symptoms: [],
      rootCause: null,
      workaround: null,
      resolution: 'Replaced it.',
      incidentNumbers: [],
    },
  })
  renderPage()
  await screen.findByText('Second floor access point drops clients')
  await userEvent.type(screen.getByLabelText('Resolution'), 'Replaced it.')
  await userEvent.click(screen.getByRole('button', { name: 'Closed' }))

  await screen.findByRole('dialog', { name: 'Knowledge article draft' })
  expect(screen.getAllByText('Not recorded — the article will need this.').length).toBe(2)
  expect(screen.getByText('No incidents were linked to this problem.')).toBeInTheDocument()
})

test('a transition the server refuses shows its reason rather than failing silently', async () => {
  vi.mocked(problemsApi.transition).mockRejectedValue(
    new Error('A problem cannot go from Closed to KnownError.'))
  renderPage()
  await screen.findByText('Second floor access point drops clients')
  await userEvent.type(screen.getByLabelText('Resolution'), 'Replaced it.')

  await userEvent.click(screen.getByRole('button', { name: 'Closed' }))

  expect(await screen.findByRole('alert'))
    .toHaveTextContent('A problem cannot go from Closed to KnownError.')
})

test('the incidents it explains are listed and link to their tickets', async () => {
  renderPage()

  expect(await screen.findByRole('link', { name: 'Wi-Fi keeps dropping' }))
    .toHaveAttribute('href', '/tickets/tkt-1')
  expect(screen.getByText('INC-000002')).toBeInTheDocument()
})

test('unlinking an incident asks the API to remove just that one', async () => {
  vi.mocked(problemsApi.unlinkIncident).mockResolvedValue(undefined)
  renderPage()
  await screen.findByRole('link', { name: 'Wi-Fi keeps dropping' })

  await userEvent.click(screen.getByRole('button', { name: 'Unlink INC-000001' }))

  await waitFor(() => expect(problemsApi.unlinkIncident).toHaveBeenCalledWith('prb-1', 'tkt-1'))
})

test('linking searches incidents and attaches the one that is picked', async () => {
  vi.mocked(helpdeskApi.listTickets).mockResolvedValue({
    items: [{ id: 'tkt-9', number: 'INC-000009', title: 'Wi-Fi drops in the boardroom' } as never],
    total: 1,
    page: 1,
    pageSize: 25,
  })
  vi.mocked(problemsApi.linkIncident).mockResolvedValue(investigating)
  renderPage()
  await screen.findByRole('link', { name: 'Wi-Fi keeps dropping' })

  await userEvent.click(screen.getByRole('button', { name: /Link an incident/ }))
  await userEvent.type(screen.getByLabelText('Search incidents'), 'boardroom')

  await userEvent.click(await screen.findByRole('button', { name: 'Link INC-000009' }))

  await waitFor(() => expect(problemsApi.linkIncident).toHaveBeenCalledWith('prb-1', 'tkt-9'))
})

/**
 * A problem with nothing attached says what that costs, because closing one is what writes its incidents
 * up and a problem with none produces an article with no symptoms in it.
 */
test('a problem with no incidents explains why linking them matters', async () => {
  vi.mocked(problemsApi.get).mockResolvedValue({ ...investigating, incidentCount: 0, incidents: [] })
  renderPage()

  expect(await screen.findByText(/A problem with no incidents is a hunch/)).toBeInTheDocument()
})

test('a deleted configuration item is named as gone rather than shown as an id', async () => {
  vi.mocked(problemsApi.get).mockResolvedValue({
    ...investigating,
    subject: { scope: 'Ci', id: 'ci-1', name: null, type: null },
  })
  renderPage()

  expect(await screen.findAllByText('A configuration item that no longer exists'))
    .not.toHaveLength(0)
})
