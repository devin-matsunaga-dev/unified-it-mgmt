import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { softwareApi, type LicensePool, type SoftwareProduct } from '../../api/software'
import { LicensePoolPage } from './LicensePoolPage'

vi.mock('../../api/software', async (original) => {
  const actual = await original<typeof import('../../api/software')>()
  return {
    ...actual,
    softwareApi: {
      listPools: vi.fn(),
      listProducts: vi.fn(),
      createPool: vi.fn(),
      updatePool: vi.fn(),
      deletePool: vi.fn(),
    },
  }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const products: SoftwareProduct[] = [
  { id: 'p-acrobat', name: 'Acrobat Pro', publisher: 'Adobe', category: null, notes: null, isActive: true, ruleCount: 1, licensePoolCount: 1, installCount: 5, createdAt: '', updatedAt: '' },
]

const dated: LicensePool = {
  id: 'pool-1',
  productId: 'p-acrobat',
  productName: 'Acrobat Pro',
  publisher: 'Adobe',
  name: 'Acrobat Pro volume subscription',
  reference: 'PO-2025-0410',
  entitlements: 3,
  purchaseDate: '2025-07-08',
  expiresAt: '2026-08-18',
  notes: null,
  isActive: true,
  status: 'ExpiringSoon',
  daysRemaining: 5,
  createdAt: '',
  updatedAt: '',
}

const perpetual: LicensePool = {
  ...dated,
  id: 'pool-2',
  name: 'Windows 11 Pro OEM entitlements',
  entitlements: 25,
  expiresAt: null,
  status: null,
  daysRemaining: null,
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><LicensePoolPage /></QueryClientProvider></MemoryRouter>)
}

describe('LicensePoolPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(softwareApi.listProducts).mockResolvedValue({ items: products, total: 1, page: 1, pageSize: 200 })
    vi.mocked(softwareApi.listPools).mockResolvedValue({ items: [dated, perpetual], total: 2, page: 1, pageSize: 25 })
  })

  /** A licence with no end date has no status; saying "Active" for it would be a claim nothing made. */
  it('reads a perpetual pool as having no end date rather than as active', async () => {
    renderPage()

    expect(await screen.findByText('Acrobat Pro volume subscription')).toBeInTheDocument()
    const table = within(screen.getByRole('table'))
    expect(table.getByText('Expiring soon')).toBeInTheDocument()
    expect(table.getByText('Perpetual')).toBeInTheDocument()
    expect(table.getByText('No end date')).toBeInTheDocument()
  })

  it('creates a pool from the dialog', async () => {
    vi.mocked(softwareApi.createPool).mockResolvedValue(dated)

    renderPage()
    await screen.findByText('Acrobat Pro volume subscription')
    await userEvent.click(screen.getByRole('button', { name: /New licence pool/ }))
    await userEvent.selectOptions(screen.getByLabelText('Product'), 'p-acrobat')
    await userEvent.type(screen.getByLabelText('Pool name'), 'Acrobat top-up')
    await userEvent.clear(screen.getByLabelText('Entitlements'))
    await userEvent.type(screen.getByLabelText('Entitlements'), '2')
    await userEvent.click(screen.getByRole('button', { name: 'Create pool' }))

    await waitFor(() => expect(softwareApi.createPool).toHaveBeenCalledWith(
      expect.objectContaining({ productId: 'p-acrobat', name: 'Acrobat top-up', entitlements: 2 })))
  })

  /** Deleting an entitlement block changes what the estate is compliant against, so it asks first. */
  it('asks before deleting a pool', async () => {
    vi.mocked(softwareApi.deletePool).mockResolvedValue(undefined)

    renderPage()
    await screen.findByText('Acrobat Pro volume subscription')
    await userEvent.click(screen.getByRole('button', { name: 'Delete Acrobat Pro volume subscription' }))

    expect(softwareApi.deletePool).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Confirm delete' }))
    await waitFor(() => expect(softwareApi.deletePool).toHaveBeenCalledWith('pool-1'))
  })

  it('refuses a licence that expires before it was bought', async () => {
    renderPage()
    await screen.findByText('Acrobat Pro volume subscription')
    await userEvent.click(screen.getByRole('button', { name: /New licence pool/ }))
    await userEvent.selectOptions(screen.getByLabelText('Product'), 'p-acrobat')
    await userEvent.type(screen.getByLabelText('Pool name'), 'Backdated')
    await userEvent.type(screen.getByLabelText('Purchased'), '2026-08-13')
    await userEvent.type(screen.getByLabelText(/^Expires/), '2026-08-01')

    expect(screen.getByText('A licence cannot expire before it was bought.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Create pool' })).toBeDisabled()
  })
})
