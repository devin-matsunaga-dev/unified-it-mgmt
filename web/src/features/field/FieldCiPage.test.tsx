import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci, type CiLifecycleStateInfo } from '../../api/assets'
import { FieldCiPage } from './FieldCiPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return {
    ...actual,
    assetsApi: {
      ...actual.assetsApi,
      getCi: vi.fn(),
      listLifecycleStates: vi.fn(),
      transitionCi: vi.fn(),
    },
  }
})

const laptop: Ci = {
  id: 'ci-1', type: 'Hardware', name: 'Reception laptop', assetTag: 'LT-00421', serialNumber: '5CD1234ABC',
  description: null, isActive: true, lifecycleState: 'Deployed',
  ownership: {
    ownerUserId: 'u-1', ownerName: 'Dana Reid', departmentId: 'd-1', departmentName: 'Support',
    siteId: 's-1', siteName: 'Riverside', assignedAt: '2026-08-08T00:00:00Z',
  },
  coverage: { contractId: null, contractName: null, poNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: {}, customFields: [], createdAt: '2026-08-08T00:00:00Z', updatedAt: '2026-08-08T00:00:00Z',
}

const states: CiLifecycleStateInfo[] = [
  { state: 'Deployed', allowedTargets: ['InRepair', 'Retired'] },
  { state: 'InRepair', allowedTargets: ['Deployed'] },
] as CiLifecycleStateInfo[]

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/ci/ci-1']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/ci/:id" element={<FieldCiPage />} />
        <Route path="/field/scan" element={<h1>Scan screen</h1>} />
        <Route path="/field/ci/:id/ticket" element={<h1>Ticket form</h1>} />
        <Route path="/field/ci/:id/assign" element={<h1>Hand over form</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('FieldCiPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    localStorage.clear()
    vi.mocked(assetsApi.listLifecycleStates).mockResolvedValue(states)
  })

  it('leads with what identifies the asset in the hand', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop)

    renderPage()

    expect(await screen.findByRole('heading', { name: 'Reception laptop' })).toBeInTheDocument()
    expect(screen.getByText('LT-00421')).toBeInTheDocument()
    expect(screen.getByText('5CD1234ABC')).toBeInTheDocument()
    expect(screen.getByText('Dana Reid')).toBeInTheDocument()
    expect(screen.getByText('Riverside · Support')).toBeInTheDocument()
    expect(screen.getByText('Deployed')).toBeInTheDocument()
  })

  it('offers only the states the server allows, and moves on tap', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop)
    vi.mocked(assetsApi.transitionCi).mockResolvedValue({ ...laptop, lifecycleState: 'InRepair' })

    renderPage()

    expect(await screen.findByRole('button', { name: /In repair/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Retired/ })).toBeInTheDocument()
    // Ordered is a state, but not one Deployed may move to — the server's graph decides, not the phone.
    expect(screen.queryByRole('button', { name: /Ordered/ })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /In repair/ }))

    await waitFor(() => expect(assetsApi.transitionCi).toHaveBeenCalledWith('ci-1', 'InRepair', null))
  })

  it('says so plainly when the label points at nothing', async () => {
    vi.mocked(assetsApi.getCi).mockRejectedValue(new Error('Not found'))

    renderPage()

    expect(await screen.findByRole('alert')).toHaveTextContent('Asset not found')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })

  it('leads the cover with the status, because that is what decides repair or replace', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue({
      ...laptop,
      coverage: {
        ...laptop.coverage, contractName: 'Dell ProSupport', vendorName: 'Dell',
        warrantyExpiresAt: '2027-03-12', warrantyStatus: 'Active', warrantyDaysRemaining: 204,
      },
    })

    renderPage()

    expect(await screen.findByText('Active')).toBeInTheDocument()
    expect(screen.getByText('ends in 204 days')).toBeInTheDocument()
    expect(screen.getByText('Dell ProSupport')).toBeInTheDocument()
  })

  it('words an expired warranty as expired rather than ending', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue({
      ...laptop,
      coverage: {
        ...laptop.coverage, warrantyExpiresAt: '2026-08-12',
        warrantyStatus: 'Expired', warrantyDaysRemaining: -8,
      },
    })

    renderPage()

    expect(await screen.findByText('Expired')).toBeInTheDocument()
    expect(screen.getByText('expired 8 days ago')).toBeInTheDocument()
  })

  it('says plainly when nothing covers the asset, rather than showing an empty block', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop)

    renderPage()

    expect(await screen.findByText(/No warranty or contract is recorded/)).toBeInTheDocument()
  })

  it('offers no way into the agent shell, which a handset cannot use', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop)

    renderPage()

    await screen.findByRole('heading', { name: 'Reception laptop' })
    expect(screen.queryByRole('link', { name: /full page/i })).not.toBeInTheDocument()
    expect(document.querySelector('a[href^="/assets/"]')).toBeNull()
  })

  it('raises a ticket for the asset in hand', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop)

    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: /Open a ticket/ }))

    expect(await screen.findByRole('heading', { name: 'Ticket form' })).toBeInTheDocument()
  })

  it('reaches the hand-over screen from the asset', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop)

    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: /Hand over or move/ }))

    expect(await screen.findByRole('heading', { name: 'Hand over form' })).toBeInTheDocument()
  })

  it('goes back to scanning without a trip through the desktop app', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop)

    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: /Scan next/ }))

    expect(await screen.findByRole('heading', { name: 'Scan screen' })).toBeInTheDocument()
  })
})
