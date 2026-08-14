import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { directoryApi } from '../../api/directory'
import { reconciliationApi, type DriftReport } from '../../api/reconciliation'
import { DriftReportPage } from './DriftReportPage'

vi.mock('../../api/reconciliation', async (original) => {
  const actual = await original<typeof import('../../api/reconciliation')>()
  return { ...actual, reconciliationApi: { getDrift: vi.fn() } }
})

vi.mock('../../api/directory', () => ({ directoryApi: { listSites: vi.fn() } }))

const report: DriftReport = {
  summary: {
    cisObserved: 7,
    cisWithDrift: 2,
    changed: 2,
    new: 0,
    missing: 1,
    unrecordedLinks: 1,
    unmatchedDiscoveries: 3,
    staleAfterDays: 7,
    generatedAt: '2026-08-14T09:00:00Z',
  },
  items: [
    {
      ciId: 'ci-hq-sw-01',
      name: 'HQ floor 1 switch',
      type: 'NetworkDevice',
      siteName: 'Head Office',
      address: '10.20.0.2',
      lastSeenAt: '2026-08-14T08:55:00Z',
      findings: [
        { field: 'location', label: 'Location', kind: 'Changed', recordedValue: 'Head Office', observedValue: 'Primary Data Centre' },
        { field: 'managementIp', label: 'Management IP', kind: 'Changed', recordedValue: '10.20.0.3', observedValue: '10.20.0.53' },
      ],
    },
    {
      ciId: 'ci-br1-sw-01',
      name: 'Branch switch',
      type: 'NetworkDevice',
      siteName: 'Regional Branch',
      address: '10.30.0.2',
      lastSeenAt: '2026-07-13T09:00:00Z',
      findings: [
        { field: 'lastSeen', label: 'Last seen by discovery', kind: 'Missing', recordedValue: null, observedValue: '2026-07-13T09:00:00Z' },
      ],
    },
  ],
  unrecordedLinks: [
    {
      sourceCiId: 'ci-dc1-sw-01',
      sourceCiName: 'DC1 core switch A',
      sourcePort: 'GigabitEthernet0/2',
      targetCiId: 'ci-dc1-sw-02',
      targetCiName: 'DC1 core switch B',
      targetPort: 'GigabitEthernet0/2',
      protocols: ['lldp'],
      confirmedByBothEnds: false,
    },
  ],
  total: 2,
  page: 1,
  pageSize: 100,
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><DriftReportPage /></MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.mocked(directoryApi.listSites).mockResolvedValue([{ id: 'site-hq', code: 'HQ', name: 'Head Office' }])
  vi.mocked(reconciliationApi.getDrift).mockResolvedValue(report)
})

afterEach(() => vi.clearAllMocks())

test('renders one row per finding with both sides of the disagreement', async () => {
  renderPage()

  const location = await screen.findByRole('row', { name: /Location/ })
  expect(within(location).getByText('Head Office')).toBeInTheDocument()
  expect(within(location).getByText('Primary Data Centre')).toBeInTheDocument()
  expect(within(location).getByText('Changed')).toBeInTheDocument()

  // The CI is named once even though it carries two findings: a switch with two disagreements is one
  // thing to go and look at.
  expect(screen.getAllByRole('link', { name: 'HQ floor 1 switch' })).toHaveLength(1)
})

test('states the staleness threshold the report was computed with', async () => {
  renderPage()

  expect(await screen.findByText(/unseen for more than 7 days counts as missing/)).toBeInTheDocument()
})

test('points at the review queue when discoveries answer to no CI', async () => {
  renderPage()

  expect(await screen.findByText(/3 discoveries answer to no CI at all/)).toBeInTheDocument()
})

test('lists a cable no relationship records', async () => {
  renderPage()

  expect(await screen.findByRole('link', { name: 'DC1 core switch A' })).toBeInTheDocument()
  expect(screen.getByText(/reported by one end/)).toBeInTheDocument()
})

test('narrowing to one kind of finding asks the API for it', async () => {
  renderPage()
  await screen.findByRole('row', { name: /Location/ })

  await userEvent.selectOptions(screen.getByLabelText('Filter by finding'), 'Missing')

  await waitFor(() => expect(reconciliationApi.getDrift).toHaveBeenLastCalledWith(
    expect.objectContaining({ kind: 'Missing' })))
})

test('an empty report says whether anything has been scanned rather than "no data"', async () => {
  vi.mocked(reconciliationApi.getDrift).mockResolvedValue({
    ...report,
    summary: { ...report.summary, cisObserved: 0, cisWithDrift: 0, changed: 0, missing: 0, unrecordedLinks: 0, unmatchedDiscoveries: 0 },
    items: [],
    unrecordedLinks: [],
    total: 0,
  })

  renderPage()

  expect(await screen.findByText('No scan has reported a known CI yet')).toBeInTheDocument()
})

test('a failed read reads Unavailable rather than zero', async () => {
  vi.mocked(reconciliationApi.getDrift).mockRejectedValue(new Error('Postgres is away'))

  renderPage()

  expect(await screen.findByText('The drift report could not be loaded')).toBeInTheDocument()
  expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(0)
})
