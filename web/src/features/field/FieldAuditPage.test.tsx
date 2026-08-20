import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { ApiError } from '../../api/client'
import { reconciliationApi, type AuditScan, type AuditSession } from '../../api/reconciliation'
import { FieldAuditPage } from './FieldAuditPage'

vi.mock('../../api/reconciliation', async (original) => {
  const actual = await original<typeof import('../../api/reconciliation')>()
  return {
    ...actual,
    reconciliationApi: {
      ...actual.reconciliationApi,
      getAuditSession: vi.fn(),
      recordAuditScan: vi.fn(),
    },
  }
})

/**
 * The camera is driven by the hook; these tests exercise what a read produces rather than the video
 * pipeline, so the hook is stubbed to hand the page one code on demand.
 */
let emitCode: (code: string) => void = () => {}
vi.mock('./useQrCamera', () => ({
  useQrCamera: (onCode: (code: string) => void) => {
    emitCode = onCode
    return { videoRef: { current: null }, status: 'scanning', start: vi.fn(), stop: vi.fn() }
  },
}))

const session: AuditSession = {
  id: 'a-1', name: 'Riverside store room', siteId: 's-1', siteName: 'Riverside', status: 'Open',
  openedBy: 'tech1', openedAt: '2026-08-20T08:00:00Z', closedBy: null, closedAt: null, note: null,
  expectedCount: 12, scannedCount: 3, unscannedCount: 9, unexpectedCount: 0,
}

const scan = (over: Partial<AuditScan>): AuditScan => ({
  id: 's-1', sessionId: 'a-1', ciId: 'ci-1', ciName: 'Reception laptop', ciType: 'Hardware',
  assetTag: 'LT-00421', serialNumber: null, code: 'LT-00421', scannedBy: 'tech1',
  scannedAt: '2026-08-20T09:00:00Z', note: null, alreadyScanned: false, expected: true,
  unexpectedReason: null, ...over,
})

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/audits/a-1']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/audits/:id" element={<FieldAuditPage />} />
        <Route path="/field/audits" element={<h1>Counts list</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('FieldAuditPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(reconciliationApi.getAuditSession).mockResolvedValue(session)
  })

  it('shows what the count has found against what it owes', async () => {
    renderPage()

    expect(await screen.findByRole('heading', { name: 'Riverside store room' })).toBeInTheDocument()
    expect(screen.getByText('Counted').previousSibling).toHaveTextContent('3/12')
    expect(screen.getByText('Still owed').previousSibling).toHaveTextContent('9')
  })

  it('records a scan and keeps scanning rather than navigating away', async () => {
    vi.mocked(reconciliationApi.recordAuditScan).mockResolvedValue(scan({}))

    renderPage()
    await screen.findByRole('heading', { name: 'Riverside store room' })
    emitCode('LT-00421')

    expect(await screen.findByText('Reception laptop')).toBeInTheDocument()
    expect(reconciliationApi.recordAuditScan).toHaveBeenCalledWith('a-1', { code: 'LT-00421' })
    // Still on the count: the whole point is label after label without touching the screen.
    expect(screen.queryByRole('heading', { name: 'Counts list' })).not.toBeInTheDocument()
  })

  it('distinguishes an asset counted twice from one counted once', async () => {
    vi.mocked(reconciliationApi.recordAuditScan).mockResolvedValue(scan({ alreadyScanned: true }))

    renderPage()
    await screen.findByRole('heading', { name: 'Riverside store room' })
    emitCode('LT-00421')

    expect(await screen.findByText('Already counted')).toBeInTheDocument()
  })

  it('records an unexpected find rather than refusing it, and says why', async () => {
    vi.mocked(reconciliationApi.recordAuditScan).mockResolvedValue(
      scan({ expected: false, unexpectedReason: 'DifferentSite' }))

    renderPage()
    await screen.findByRole('heading', { name: 'Riverside store room' })
    emitCode('LT-00421')

    expect(await screen.findByText(/Counted, but/)).toBeInTheDocument()
  })

  it('reports a code that matches no asset without stopping the count', async () => {
    vi.mocked(reconciliationApi.recordAuditScan).mockRejectedValue(new ApiError(404, 'No asset matches that code.'))

    renderPage()
    await screen.findByRole('heading', { name: 'Riverside store room' })
    emitCode('NOT-A-TAG')

    expect(await screen.findByText('No asset carries that code')).toBeInTheDocument()
    expect(screen.getByText('NOT-A-TAG')).toBeInTheDocument()
  })

  it('surfaces a failed save instead of silently dropping the scan', async () => {
    vi.mocked(reconciliationApi.recordAuditScan).mockRejectedValue(new Error('Network request failed'))

    renderPage()
    await screen.findByRole('heading', { name: 'Riverside store room' })
    emitCode('LT-00421')

    expect(await screen.findByText('That scan did not save')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('Network request failed')).toBeInTheDocument())
  })
})
