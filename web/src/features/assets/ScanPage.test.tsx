import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci } from '../../api/assets'
import { ApiError } from '../../api/client'
import { ScanPage } from './ScanPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, lookupCi: vi.fn() } }
})

const laptop: Ci = {
  id: 'ci-1', type: 'Hardware', name: 'Reception laptop', assetTag: 'LT-00421', serialNumber: '5CD1234ABC',
  description: null, isActive: true, lifecycleState: 'Deployed',
  ownership: { ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteId: null, siteName: null, assignedAt: null },
  coverage: { contractId: null, contractName: null, poNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: { manufacturer: 'Dell', model: 'Latitude 5450' },
  customFields: [], createdAt: '2026-08-08T00:00:00Z', updatedAt: '2026-08-08T00:00:00Z',
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/scan']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/scan" element={<ScanPage />} />
        <Route path="/assets/:id" element={<h1>Asset page</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('ScanPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    sessionStorage.clear()
  })

  it('remembers a scan across the navigation that unmounts the page', async () => {
    vi.mocked(assetsApi.lookupCi).mockResolvedValue(laptop)
    const first = renderPage()

    await userEvent.type(screen.getByLabelText(/Asset tag, serial number, or scanned code/), 'LT-00421{Enter}')
    expect(await screen.findByRole('heading', { name: 'Asset page' })).toBeInTheDocument()
    first.unmount()

    // Coming back to /scan — as a stock-take does between every asset — still lists what was scanned.
    renderPage()
    expect(await screen.findByText('Scanned in this session')).toBeInTheDocument()
    expect(screen.getByText('Reception laptop')).toBeInTheDocument()
  })

  it('looks up a scanned asset tag and opens its asset page', async () => {
    vi.mocked(assetsApi.lookupCi).mockResolvedValue(laptop)
    renderPage()

    await userEvent.type(screen.getByLabelText(/Asset tag, serial number, or scanned code/), 'LT-00421')
    await userEvent.click(screen.getByRole('button', { name: 'Find' }))

    await waitFor(() => expect(assetsApi.lookupCi).toHaveBeenCalledWith('LT-00421'))
    expect(await screen.findByRole('heading', { name: 'Asset page' })).toBeInTheDocument()
  })

  it('submits on Enter, which is all a wedge scanner sends', async () => {
    vi.mocked(assetsApi.lookupCi).mockResolvedValue(laptop)
    renderPage()

    await userEvent.type(screen.getByLabelText(/Asset tag, serial number, or scanned code/), '5CD1234ABC{Enter}')

    await waitFor(() => expect(assetsApi.lookupCi).toHaveBeenCalledWith('5CD1234ABC'))
  })

  it('trims what the scanner sent before looking it up', async () => {
    vi.mocked(assetsApi.lookupCi).mockResolvedValue(laptop)
    renderPage()

    await userEvent.type(screen.getByLabelText(/Asset tag, serial number, or scanned code/), '  LT-00421  {Enter}')

    await waitFor(() => expect(assetsApi.lookupCi).toHaveBeenCalledWith('LT-00421'))
  })

  it('names the code that matched nothing rather than navigating', async () => {
    vi.mocked(assetsApi.lookupCi).mockRejectedValue(new ApiError(404, 'No asset matches that code.'))
    renderPage()

    await userEvent.type(screen.getByLabelText(/Asset tag, serial number, or scanned code/), 'LT-99999{Enter}')

    expect(await screen.findByRole('alert')).toHaveTextContent('LT-99999')
    expect(screen.queryByRole('heading', { name: 'Asset page' })).not.toBeInTheDocument()
  })

  it('surfaces a non-404 failure as its own message', async () => {
    vi.mocked(assetsApi.lookupCi).mockRejectedValue(new ApiError(500, 'The request could not be completed.'))
    renderPage()

    await userEvent.type(screen.getByLabelText(/Asset tag, serial number, or scanned code/), 'LT-00421{Enter}')

    expect(await screen.findByRole('alert')).toHaveTextContent('The request could not be completed.')
  })

  it('cannot be submitted empty', async () => {
    renderPage()

    expect(screen.getByRole('button', { name: 'Find' })).toBeDisabled()
    expect(assetsApi.lookupCi).not.toHaveBeenCalled()
  })
})
