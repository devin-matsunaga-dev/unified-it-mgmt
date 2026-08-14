import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { directoryApi } from '../../api/directory'
import { reconciliationApi, type AuditSession, type AuditSessionPage } from '../../api/reconciliation'
import { AuditSessionsPage } from './AuditSessionsPage'

vi.mock('../../api/reconciliation', async (original) => {
  const actual = await original<typeof import('../../api/reconciliation')>()
  return {
    ...actual,
    reconciliationApi: { listAuditSessions: vi.fn(), createAuditSession: vi.fn() },
  }
})

vi.mock('../../api/directory', () => ({ directoryApi: { listSites: vi.fn() } }))
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() } }))

const page: AuditSessionPage = {
  items: [
    {
      id: 'session-1', name: 'Q3 data centre count', siteId: 'site-dc1', siteName: 'Primary Data Centre',
      status: 'Open', openedBy: 'technician1', openedAt: '2026-08-14T08:00:00Z',
      closedBy: null, closedAt: null, note: null, scanCount: 3,
    },
  ],
  total: 1,
  page: 1,
  pageSize: 25,
}

const created: AuditSession = {
  id: 'session-2', name: 'Head Office count', siteId: 'site-hq', siteName: 'Head Office',
  status: 'Open', openedBy: 'technician1', openedAt: '2026-08-14T09:00:00Z',
  closedBy: null, closedAt: null, note: null,
  expectedCount: 12, scannedCount: 0, unscannedCount: 12, unexpectedCount: 0,
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><AuditSessionsPage /></MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.mocked(directoryApi.listSites).mockResolvedValue([{ id: 'site-hq', code: 'HQ', name: 'Head Office' }])
  vi.mocked(reconciliationApi.listAuditSessions).mockResolvedValue(page)
  vi.mocked(reconciliationApi.createAuditSession).mockResolvedValue(created)
})

afterEach(() => vi.clearAllMocks())

test('lists counts with the scope each one walked', async () => {
  renderPage()

  expect(await screen.findByRole('link', { name: 'Q3 data centre count' })).toBeInTheDocument()
  expect(screen.getByText('Primary Data Centre')).toBeInTheDocument()
})

test('starting a count sends the chosen site', async () => {
  renderPage()
  await screen.findByRole('link', { name: 'Q3 data centre count' })

  await userEvent.click(screen.getByRole('button', { name: /Start a count/ }))
  await userEvent.type(screen.getByLabelText('Name'), 'Head Office count')
  await userEvent.selectOptions(screen.getByLabelText('Site'), 'site-hq')
  await userEvent.click(screen.getByRole('button', { name: 'Start counting' }))

  await waitFor(() => expect(reconciliationApi.createAuditSession)
    .toHaveBeenCalledWith({ name: 'Head Office count', siteId: 'site-hq', note: null }))
})

test('the whole estate is an offered scope rather than a missing one', async () => {
  renderPage()

  await userEvent.click(screen.getByRole('button', { name: /Start a count/ }))

  expect(screen.getByRole('option', { name: 'The whole estate' })).toBeInTheDocument()
})

test('an empty list explains what a count does rather than saying "no data"', async () => {
  vi.mocked(reconciliationApi.listAuditSessions).mockResolvedValue({ ...page, items: [], total: 0 })

  renderPage()

  expect(await screen.findByText('Nobody has counted anything yet')).toBeInTheDocument()
})
