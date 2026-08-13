import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { softwareApi, type InstalledSoftware } from '../../api/software'
import { CiSoftwareCard } from './CiSoftwareCard'

vi.mock('../../api/software', async (original) => {
  const actual = await original<typeof import('../../api/software')>()
  return { ...actual, softwareApi: { listInstalls: vi.fn() } }
})

const install = (overrides: Partial<InstalledSoftware>): InstalledSoftware => ({
  id: 'i-1',
  ciId: 'ci-1',
  ciName: 'Laptop LT-0001',
  rawName: 'Google Chrome',
  rawPublisher: 'Google LLC',
  version: '121.0.6167.140',
  productId: 'p-1',
  productName: 'Chrome',
  productPublisher: 'Google',
  installedOn: '2026-07-14',
  source: 'seeded-inventory.csv',
  firstSeenAt: '2026-08-13T12:00:00Z',
  lastSeenAt: '2026-08-13T12:00:00Z',
  sightingCount: 1,
  ...overrides,
})

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><CiSoftwareCard ciId="ci-1" /></QueryClientProvider></MemoryRouter>)
}

describe('CiSoftwareCard', () => {
  beforeEach(() => vi.clearAllMocks())

  it('shows the raw name the machine reported beside what the catalogue made of it', async () => {
    vi.mocked(softwareApi.listInstalls).mockResolvedValue({
      items: [install({}), install({ id: 'i-2', rawName: 'Contoso VPN Client', productId: null, productName: null })],
      total: 2, page: 1, pageSize: 200,
    })

    renderCard()

    expect(await screen.findByText('Google Chrome')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Chrome' })).toHaveAttribute('href', '/software/products/p-1')
    // A name no rule claims says so rather than being hidden or guessed at.
    expect(screen.getByText('Unrecognised')).toBeInTheDocument()
  })

  it('says no inventory has been imported rather than printing nothing', async () => {
    vi.mocked(softwareApi.listInstalls).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 })

    renderCard()

    expect(await screen.findByText(/No inventory has been imported for this asset/)).toBeInTheDocument()
  })

  /** The failure path: an unreadable list must not read as a machine with nothing installed. */
  it('distinguishes an empty inventory from an unreadable one', async () => {
    vi.mocked(softwareApi.listInstalls).mockRejectedValue(new Error('boom'))

    renderCard()

    expect(await screen.findByRole('alert')).toHaveTextContent('Installed software could not be loaded')
    expect(screen.queryByText(/No inventory has been imported/)).not.toBeInTheDocument()
  })
})
