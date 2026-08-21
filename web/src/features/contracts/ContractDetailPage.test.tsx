import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci } from '../../api/assets'
import { contractsApi, type Contract } from '../../api/contracts'
import { ContractDetailPage } from './ContractDetailPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, listCis: vi.fn() } }
})

vi.mock('../../api/contracts', async (original) => {
  const actual = await original<typeof import('../../api/contracts')>()
  return { ...actual, contractsApi: Object.fromEntries(Object.entries(actual.contractsApi).map(([name, value]) => [name, typeof value === 'function' ? vi.fn() : value])) }
})

vi.mock('../../api/directory', () => ({ directoryApi: { listUsers: vi.fn().mockResolvedValue([]) } }))

const contract: Contract = {
  id: 'contract-1', vendorId: 'vendor-1', vendorName: 'Dell', poNumber: 'C-1001', name: 'ProSupport Plus',
  type: 'Support', startDate: '2025-09-01', endDate: '2026-09-01', autoRenews: true, cost: 12500, currency: 'USD',
  ownerUserId: 'user-1',
  departmentId: null,
  departmentName: null,
  contractNumber: null, ownerName: 'Technician One', ownerEmail: 'tech1@example.test', notes: 'Covers the racks',
  isActive: true, status: 'ExpiringSoon', daysRemaining: 24, coveredCiCount: 1,
  createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z',
}

const coveredCi: Ci = {
  id: 'ci-host', type: 'Server', name: 'esx-01', assetTag: 'AT-9001', serialNumber: 'SN-9001', description: null,
  isActive: true, lifecycleState: 'Deployed',
  ownership: { ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteId: null, siteName: null, assignedAt: null },
  coverage: {
    contractId: contract.id, contractName: contract.name, poNumber: contract.poNumber,
    vendorName: 'Dell', contractEndDate: contract.endDate, purchaseDate: '2025-09-01',
    warrantyExpiresAt: '2026-09-01', warrantyStatus: 'ExpiringSoon', warrantyDaysRemaining: 24,
  },
  attributes: {}, customFields: [], createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z',
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={[`/contracts/${contract.id}`]}><QueryClientProvider client={client}>
    <Routes><Route path="/contracts/:id" element={<ContractDetailPage />} /></Routes>
  </QueryClientProvider></MemoryRouter>)
}

describe('ContractDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(contractsApi.getContract).mockResolvedValue(contract)
    vi.mocked(contractsApi.listVendors).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 })
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [coveredCi], total: 1, page: 1, pageSize: 200 })
  })

  it('shows the terms and how long the contract has left', async () => {
    renderPage()

    expect(await screen.findByRole('heading', { name: 'ProSupport Plus' })).toBeInTheDocument()
    expect(screen.getByText('Expiring soon')).toBeInTheDocument()
    expect(screen.getByText(/ends 2026-09-01 \(in 24 days\)/)).toBeInTheDocument()
    expect(screen.getByText('Notices to').parentElement).toHaveTextContent('tech1@example.test')
  })

  /** The WP's second verification step: the contract page lists the CIs it covers. */
  it('asks the API only for the CIs this contract covers and lists them', async () => {
    renderPage()

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith({ contractId: contract.id, pageSize: 200 }))
    const covered = (await screen.findByRole('heading', { name: 'Covered assets' })).closest('section')!
    expect(within(covered).getByRole('link', { name: 'esx-01' })).toHaveAttribute('href', '/assets/ci-host')
    expect(within(covered).getByText('Warranty ends 2026-09-01')).toBeInTheDocument()
  })

  it('explains where coverage is attached when nothing is covered yet', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 })
    renderPage()

    expect(await screen.findByText(/Nothing is covered yet/)).toBeInTheDocument()
  })

  it('offers a retry when the contract cannot be loaded', async () => {
    vi.mocked(contractsApi.getContract).mockRejectedValue(new Error('Contract not found.'))
    renderPage()

    expect(await screen.findByText('Contract could not be loaded')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
})
