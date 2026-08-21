import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci, type CiPage } from '../../api/assets'
import { FieldScanPage } from './FieldScanPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, lookupCi: vi.fn(), listCis: vi.fn() } }
})
vi.mock('./useQrCamera', () => ({
  useQrCamera: () => ({ videoRef: { current: null }, status: 'idle', start: vi.fn(), stop: vi.fn() }),
}))

const ci = (over: Partial<Ci>): Ci => ({
  id: 'ci-1', type: 'Hardware', name: 'Reception laptop', assetTag: 'LT-00421',
  serialNumber: '5CD1234ABC', description: null, isActive: true, lifecycleState: 'Deployed',
  ownership: { ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteId: null, siteName: null, assignedAt: null },
  coverage: { contractId: null, contractName: null, poNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: {}, customFields: [], createdAt: '2026-08-08T00:00:00Z', updatedAt: '2026-08-08T00:00:00Z',
  ...over,
})

const page = (items: Ci[]): CiPage => ({ items, total: items.length, page: 1, pageSize: 10 })

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/scan']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/scan" element={<FieldScanPage />} />
        <Route path="/field/ci/:id" element={<h1>Field asset page</h1>} />
        <Route path="/field/receive" element={<h1>Receive</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('FieldScanPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.listCis).mockResolvedValue(page([]))
  })

  /** Nothing to press: pressing a button to learn you mistyped is a slow way to find out. */
  it('searches as the technician types, with no button to press', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue(page([ci({})]))

    renderPage()
    await userEvent.type(screen.getByLabelText(/Search by name, asset tag or serial/), 'LT-004')

    expect(await screen.findByText('Reception laptop')).toBeInTheDocument()
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenLastCalledWith({ search: 'LT-004', pageSize: 10 }))
    expect(screen.queryByRole('button', { name: /Find asset/ })).not.toBeInTheDocument()
  })

  /** One character matches most of an estate, so the list would be noise rather than a narrowing. */
  it('waits for a second character before searching at all', async () => {
    renderPage()
    await userEvent.type(screen.getByLabelText(/Search by name, asset tag or serial/), 'L')

    await waitFor(() => expect(assetsApi.listCis).not.toHaveBeenCalled())
  })

  /**
   * The debounce. Without it this is one request per keystroke, and the answers arrive out of order
   * so the list flickers between prefixes the technician has already moved past.
   */
  it('does not fire a request per keystroke', async () => {
    renderPage()
    await userEvent.type(screen.getByLabelText(/Search by name, asset tag or serial/), 'LT-00421')

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalled())
    expect(vi.mocked(assetsApi.listCis).mock.calls.length).toBeLessThan(4)
  })

  it('opens the asset a result names', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue(page([ci({})]))

    renderPage()
    await userEvent.type(screen.getByLabelText(/Search by name, asset tag or serial/), 'LT-004')
    await userEvent.click(await screen.findByText('Reception laptop'))

    expect(await screen.findByRole('heading', { name: 'Field asset page' })).toBeInTheDocument()
  })

  it('shows the tag and serial, since that is what was searched on', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue(page([ci({})]))

    renderPage()
    await userEvent.type(screen.getByLabelText(/Search by name, asset tag or serial/), 'LT-004')

    const list = await screen.findByRole('list', { name: 'Matching assets' })
    expect(within(list).getByText('LT-00421 · 5CD1234ABC')).toBeInTheDocument()
  })

  /** On a phone the likeliest reason for no match is a device that arrived this morning. */
  it('offers to receive a code that matches nothing', async () => {
    renderPage()
    await userEvent.type(screen.getByLabelText(/Search by name, asset tag or serial/), 'DNI152602HL')

    expect(await screen.findByText(/Nothing matches/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Receive it as a new asset/ }))
      .toHaveAttribute('href', '/field/receive?code=DNI152602HL&checked=1')
  })

  it('surfaces a failed search rather than showing an empty list', async () => {
    vi.mocked(assetsApi.listCis).mockRejectedValue(new Error('Network request failed'))

    renderPage()
    await userEvent.type(screen.getByLabelText(/Search by name, asset tag or serial/), 'LT-004')

    expect(await screen.findByRole('alert')).toHaveTextContent('Network request failed')
  })
})
