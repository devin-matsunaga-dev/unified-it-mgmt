import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { reconciliationApi, type AuditSessionPage } from '../../api/reconciliation'
import { FieldAuditsPage } from './FieldAuditsPage'

vi.mock('../../api/reconciliation', async (original) => {
  const actual = await original<typeof import('../../api/reconciliation')>()
  return {
    ...actual,
    reconciliationApi: { ...actual.reconciliationApi, listAuditSessions: vi.fn() },
  }
})

const page = (items: AuditSessionPage['items']): AuditSessionPage => ({ items, total: items.length, page: 1, pageSize: 50 })

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/audits']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/audits" element={<FieldAuditsPage />} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('FieldAuditsPage', () => {
  beforeEach(() => vi.clearAllMocks())

  it('asks only for open counts, because a closed one refuses every scan', async () => {
    vi.mocked(reconciliationApi.listAuditSessions).mockResolvedValue(page([{
      id: 'a-1', name: 'Riverside store room', siteId: 's-1', siteName: 'Riverside', status: 'Open',
      openedBy: 'tech1', openedAt: '2026-08-20T08:00:00Z', closedBy: null, closedAt: null,
      note: null, scanCount: 3,
    }]))

    renderPage()

    expect(await screen.findByRole('link', { name: /Riverside store room/ })).toHaveAttribute('href', '/field/audits/a-1')
    expect(reconciliationApi.listAuditSessions).toHaveBeenCalledWith('Open', 1, 50)
  })

  it('explains an empty list rather than showing a bare nothing', async () => {
    vi.mocked(reconciliationApi.listAuditSessions).mockResolvedValue(page([]))

    renderPage()

    expect(await screen.findByText(/No count is open/)).toBeInTheDocument()
  })
})
