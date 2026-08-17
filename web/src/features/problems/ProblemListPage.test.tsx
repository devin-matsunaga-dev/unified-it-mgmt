import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { problemsApi, type Problem, type ProblemPage, type ProblemSuggestion } from '../../api/problems'
import { ProblemListPage } from './ProblemListPage'

vi.mock('../../api/problems', async (original) => {
  const actual = await original<typeof import('../../api/problems')>()
  return {
    ...actual,
    problemsApi: {
      list: vi.fn(),
      listSuggestions: vi.fn(),
      detect: vi.fn(),
      acceptSuggestion: vi.fn(),
      dismissSuggestion: vi.fn(),
      create: vi.fn(),
    },
  }
})

const navigate = vi.fn()
vi.mock('react-router-dom', async (original) => {
  const actual = await original<typeof import('react-router-dom')>()
  return { ...actual, useNavigate: () => navigate }
})

const knownError: Problem = {
  id: 'prb-1',
  number: 'PRB-000001',
  title: 'Second floor access point drops clients',
  description: 'Five incidents in a week.',
  status: 'KnownError',
  priority: 'High',
  isKnownError: true,
  subject: { scope: 'Ci', id: 'ci-1', name: 'HQ floor 2 access point', type: 'NetworkDevice' },
  rootCause: 'A failing radio.',
  workaround: 'Associate to the floor 3 access point.',
  resolution: null,
  assignedTechnicianId: 'technician1',
  openedById: 'technician1',
  openedByName: 'Technician One',
  incidentCount: 5,
  createdAt: '2026-08-15T09:00:00Z',
  updatedAt: '2026-08-16T09:00:00Z',
  knownErrorAt: '2026-08-16T09:00:00Z',
  resolvedAt: null,
  closedAt: null,
}

const page: ProblemPage = { items: [knownError], total: 1, page: 1, pageSize: 100 }

const suggestion: ProblemSuggestion = {
  id: 'sug-1',
  scope: 'Ci',
  subject: { scope: 'Ci', id: 'ci-2', name: 'Branch switch', type: 'NetworkDevice' },
  incidentCount: 5,
  windowStart: '2026-08-10T00:00:00Z',
  windowEnd: '2026-08-17T00:00:00Z',
  status: 'Open',
  detectedAt: '2026-08-17T02:00:00Z',
  createdProblemId: null,
  createdProblemNumber: null,
  resolvedById: null,
  resolvedByName: null,
  resolvedAt: null,
  dismissReason: null,
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><ProblemListPage /></MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.mocked(problemsApi.list).mockResolvedValue(page)
  vi.mocked(problemsApi.listSuggestions).mockResolvedValue([suggestion])
})

afterEach(() => vi.clearAllMocks())

test('a recurrence the platform noticed is stated as a sentence with both answers beside it', async () => {
  renderPage()

  expect(await screen.findByText('Branch switch')).toBeInTheDocument()
  expect(screen.getByText('5 incidents in 7 days')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: /Make it a problem/ })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: /Not one problem/ })).toBeInTheDocument()
})

test('accepting a suggestion opens the problem it created', async () => {
  vi.mocked(problemsApi.acceptSuggestion).mockResolvedValue({
    ...suggestion,
    status: 'Accepted',
    createdProblemId: 'prb-9',
    createdProblemNumber: 'PRB-000009',
  })
  renderPage()
  await screen.findByText('Branch switch')

  await userEvent.click(screen.getByRole('button', { name: /Make it a problem/ }))

  await waitFor(() => expect(problemsApi.acceptSuggestion).toHaveBeenCalledWith('sug-1'))
  await waitFor(() => expect(navigate).toHaveBeenCalledWith('/problems/prb-9'))
})

test('dismissing asks why first, and sends the reason', async () => {
  vi.mocked(problemsApi.dismissSuggestion).mockResolvedValue({ ...suggestion, status: 'Dismissed' })
  renderPage()
  await screen.findByText('Branch switch')

  await userEvent.click(screen.getByRole('button', { name: /Not one problem/ }))
  await userEvent.type(
    screen.getByLabelText(/Why is it not one problem/), 'Unrelated faults sharing a rack')
  await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }))

  await waitFor(() => expect(problemsApi.dismissSuggestion)
    .toHaveBeenCalledWith('sug-1', 'Unrelated faults sharing a rack'))
})

/**
 * The pane stays put when there is nothing in it. A pane that vanishes reads as a feature that broke, and
 * "nothing is recurring" is worth a board saying out loud.
 */
test('an empty inbox explains itself rather than disappearing', async () => {
  vi.mocked(problemsApi.listSuggestions).mockResolvedValue([])
  renderPage()

  expect(await screen.findByText(/Nothing is recurring above the threshold/)).toBeInTheDocument()
  expect(screen.getByText('Recurrences worth a look')).toBeInTheDocument()
})

test('running the pass by hand reports what it examined even when it found nothing', async () => {
  vi.mocked(problemsApi.detect).mockResolvedValue({
    windowStart: '2026-08-10T00:00:00Z',
    windowEnd: '2026-08-17T00:00:00Z',
    minimumIncidents: 5,
    examined: 12,
    suggested: 0,
    skipped: { BelowThreshold: 11, AlreadyAProblem: 1 },
    suggestions: [],
  })
  renderPage()
  await screen.findByText('Branch switch')

  await userEvent.click(screen.getByRole('button', { name: /Look for recurrences/ }))

  await waitFor(() => expect(problemsApi.detect).toHaveBeenCalled())
})

test('the board lists a problem with what it is about and how many incidents it explains', async () => {
  renderPage()

  const row = await screen.findByRole('row', { name: /Second floor access point drops clients/ })
  expect(within(row).getByText('PRB-000001')).toBeInTheDocument()
  expect(within(row).getByText('HQ floor 2 access point')).toBeInTheDocument()
  expect(within(row).getByText('Known error')).toBeInTheDocument()
  expect(within(row).getByText('5')).toBeInTheDocument()
})

test('narrowing to known errors asks the API for them', async () => {
  renderPage()
  await screen.findByRole('row', { name: /Second floor access point/ })

  await userEvent.click(screen.getByLabelText('Known errors only'))

  await waitFor(() => expect(problemsApi.list)
    .toHaveBeenLastCalledWith(expect.objectContaining({ knownErrorsOnly: true })))
})

test('filtering by status asks the API for that status', async () => {
  renderPage()
  await screen.findByRole('row', { name: /Second floor access point/ })

  await userEvent.selectOptions(screen.getByLabelText('Filter by status'), 'KnownError')

  await waitFor(() => expect(problemsApi.list)
    .toHaveBeenLastCalledWith(expect.objectContaining({ statuses: ['KnownError'] })))
})

/** An empty known-error list says what would put something in it, rather than "no data". */
test('an empty known-error list explains what makes a problem one', async () => {
  vi.mocked(problemsApi.list).mockResolvedValue({ ...page, items: [], total: 0 })
  renderPage()
  await screen.findByText('No problems have been opened')

  await userEvent.click(screen.getByLabelText('Known errors only'))

  expect(await screen.findByText('No known errors yet')).toBeInTheDocument()
  expect(screen.getByText(/both its root cause and a workaround/)).toBeInTheDocument()
})

test('a failed read says Unavailable rather than zero', async () => {
  vi.mocked(problemsApi.list).mockRejectedValue(new Error('Postgres is away'))
  renderPage()

  expect(await screen.findByText('Problems could not be loaded')).toBeInTheDocument()
  expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(0)
})

test('opening a problem by hand goes to the problem it created', async () => {
  vi.mocked(problemsApi.create).mockResolvedValue({ ...knownError, id: 'prb-new' })
  renderPage()
  await screen.findByRole('row', { name: /Second floor access point/ })

  await userEvent.click(screen.getByRole('button', { name: /New problem/ }))
  await userEvent.type(screen.getByLabelText('Title'), 'Mail relay keeps refusing connections')
  await userEvent.type(screen.getByLabelText('What is happening'), 'Four incidents this week.')
  await userEvent.click(screen.getByRole('button', { name: 'Open problem' }))

  await waitFor(() => expect(problemsApi.create).toHaveBeenCalledWith(expect.objectContaining({
    title: 'Mail relay keeps refusing connections',
  })))
  await waitFor(() => expect(navigate).toHaveBeenCalledWith('/problems/prb-new'))
})
