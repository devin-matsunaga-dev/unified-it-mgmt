import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, test, vi } from 'vitest'
import { problemsApi, type Problem } from '../../api/problems'
import { RelatedProblemsCard } from './RelatedProblemsCard'

vi.mock('../../api/problems', async (original) => {
  const actual = await original<typeof import('../../api/problems')>()
  return { ...actual, problemsApi: { listForTicket: vi.fn() } }
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
  workaround: 'Associate to the floor 3 access point until the radio is replaced.',
  resolution: null,
  assignedTechnicianId: null,
  openedById: 'technician1',
  openedByName: 'Technician One',
  incidentCount: 5,
  createdAt: '2026-08-15T09:00:00Z',
  updatedAt: '2026-08-16T09:00:00Z',
  knownErrorAt: '2026-08-16T09:00:00Z',
  resolvedAt: null,
  closedAt: null,
}

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><RelatedProblemsCard ticketId="tkt-1" /></MemoryRouter>
    </QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

/**
 * The whole value of a known-error database is that it reaches the person holding the ticket, so the
 * workaround is rendered in full rather than behind a link.
 */
test('the workaround is shown on the incident, not just linked to', async () => {
  vi.mocked(problemsApi.listForTicket).mockResolvedValue([knownError])

  renderCard()

  expect(await screen.findByText('Part of a known problem')).toBeInTheDocument()
  expect(screen.getByText('Associate to the floor 3 access point until the radio is replaced.'))
    .toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Second floor access point drops clients' }))
    .toHaveAttribute('href', '/problems/prb-1')
  expect(screen.getByText('5 linked incidents')).toBeInTheDocument()
})

/** A problem still being investigated has no workaround to offer, and says nothing rather than nothing-shaped. */
test('a problem with no workaround yet still says the incident is not isolated', async () => {
  vi.mocked(problemsApi.listForTicket).mockResolvedValue([
    { ...knownError, status: 'Investigating', isKnownError: false, workaround: null },
  ])

  renderCard()

  expect(await screen.findByText('Part of a known problem')).toBeInTheDocument()
  expect(screen.getByText('Investigating')).toBeInTheDocument()
  expect(screen.queryByText('Workaround')).not.toBeInTheDocument()
})

/**
 * Most tickets belong to no problem, and an empty card on every ticket screen would cost more attention
 * than it ever repaid.
 */
test('a ticket that belongs to no problem renders nothing at all', async () => {
  vi.mocked(problemsApi.listForTicket).mockResolvedValue([])

  const { container } = renderCard()

  await waitFor(() => expect(problemsApi.listForTicket).toHaveBeenCalledWith('tkt-1'))
  expect(container).toBeEmptyDOMElement()
})
