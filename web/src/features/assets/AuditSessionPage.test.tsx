import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { reconciliationApi, type AuditReport, type AuditScan } from '../../api/reconciliation'
import { AuditSessionPage } from './AuditSessionPage'

vi.mock('../../api/reconciliation', async (original) => {
  const actual = await original<typeof import('../../api/reconciliation')>()
  return {
    ...actual,
    reconciliationApi: {
      getAuditReport: vi.fn(),
      recordAuditScan: vi.fn(),
      closeAuditSession: vi.fn(),
    },
  }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() } }))

const session: AuditReport['session'] = {
  id: 'session-1',
  name: 'Q3 data centre count',
  siteId: 'site-dc1',
  siteName: 'Primary Data Centre',
  status: 'Open',
  openedBy: 'technician1',
  openedAt: '2026-08-14T08:00:00Z',
  closedBy: null,
  closedAt: null,
  note: null,
  expectedCount: 5,
  scannedCount: 3,
  unscannedCount: 2,
  unexpectedCount: 1,
}

const report: AuditReport = {
  session,
  scanned: [
    {
      ciId: 'ci-1', name: 'DC1 core switch A', type: 'NetworkDevice', assetTag: 'NET-0002',
      serialNumber: 'FTX2401S001', lifecycleState: 'Deployed', siteName: 'Primary Data Centre',
      ownerName: 'Technician Two', scannedAt: '2026-08-14T08:10:00Z', scannedBy: 'technician1',
    },
  ],
  unscanned: [
    {
      ciId: 'ci-4', name: 'DC1 hypervisor host 4', type: 'Server', assetTag: 'SRV-0004',
      serialNumber: 'DL380-0004', lifecycleState: 'Deployed', siteName: 'Primary Data Centre',
      ownerName: null, scannedAt: null, scannedBy: null,
    },
  ],
  unexpected: [
    {
      ciId: 'ci-9', name: 'HQ floor 1 printer', type: 'Hardware', assetTag: 'PR-0001',
      serialNumber: 'CNB4103001', lifecycleState: 'Deployed', siteName: 'Head Office',
      reason: 'DifferentSite', scannedAt: '2026-08-14T08:20:00Z', scannedBy: 'technician1',
    },
  ],
  truncated: false,
  generatedAt: '2026-08-14T08:30:00Z',
}

const scan: AuditScan = {
  id: 'scan-1', sessionId: 'session-1', ciId: 'ci-2', ciName: 'DC1 core switch B',
  ciType: 'NetworkDevice', assetTag: 'NET-0003', serialNumber: 'FTX2401S002', code: 'NET-0003',
  scannedBy: 'technician1', scannedAt: '2026-08-14T08:25:00Z', note: null,
  alreadyScanned: false, expected: true, unexpectedReason: null,
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/audits/session-1']}>
        <Routes><Route path="/audits/:id" element={<AuditSessionPage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.mocked(reconciliationApi.getAuditReport).mockResolvedValue(report)
  vi.mocked(reconciliationApi.recordAuditScan).mockResolvedValue(scan)
  vi.mocked(reconciliationApi.closeAuditSession).mockResolvedValue({ ...session, status: 'Closed' })
})

afterEach(() => vi.clearAllMocks())

test('lists what did not turn up, which is the finding a count exists to produce', async () => {
  renderPage()

  const heading = await screen.findByRole('heading', { name: /Not found/ })
  const section = heading.closest('section')!
  expect(within(section).getByRole('link', { name: 'DC1 hypervisor host 4' })).toBeInTheDocument()
  expect(within(section).getByText('SRV-0004')).toBeInTheDocument()
})

test('reports an asset found here that the CMDB records elsewhere', async () => {
  renderPage()

  const heading = await screen.findByRole('heading', { name: 'Found but not expected' })
  const section = heading.closest('section')!
  expect(within(section).getByText('Recorded at another site')).toBeInTheDocument()
  expect(within(section).getByText('Head Office')).toBeInTheDocument()
})

test('a scan sends the typed code and clears the box for the next one', async () => {
  renderPage()
  const box = await screen.findByLabelText(/Asset tag, serial number, or scanned code/)

  await userEvent.type(box, 'NET-0003{Enter}')

  await waitFor(() => expect(reconciliationApi.recordAuditScan)
    .toHaveBeenCalledWith('session-1', { code: 'NET-0003' }))
  await waitFor(() => expect(box).toHaveValue(''))
})

test('closing asks first, because a closed count cannot be topped up', async () => {
  renderPage()

  await userEvent.click(await screen.findByRole('button', { name: /Close the count/ }))
  expect(reconciliationApi.closeAuditSession).not.toHaveBeenCalled()

  await userEvent.click(screen.getByRole('button', { name: /Confirm — close with 2 unaccounted for/ }))

  await waitFor(() => expect(reconciliationApi.closeAuditSession).toHaveBeenCalledWith('session-1'))
})

test('a closed count offers no scan box at all', async () => {
  vi.mocked(reconciliationApi.getAuditReport).mockResolvedValue({
    ...report,
    session: { ...session, status: 'Closed', closedBy: 'technician1', closedAt: '2026-08-14T09:00:00Z' },
  })

  renderPage()

  expect(await screen.findByText(/This count is closed/)).toBeInTheDocument()
  expect(screen.queryByLabelText(/Asset tag, serial number, or scanned code/)).not.toBeInTheDocument()
})

test('a session that does not exist says so rather than rendering an empty count', async () => {
  vi.mocked(reconciliationApi.getAuditReport).mockRejectedValue(
    Object.assign(new Error('Audit session not found.'), { status: 404 }))

  renderPage()

  expect(await screen.findByText('The count could not be loaded')).toBeInTheDocument()
})
