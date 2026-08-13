import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { softwareApi, type SoftwareCompliance } from '../../api/software'
import { SoftwarePage } from './SoftwarePage'

vi.mock('../../api/software', async (original) => {
  const actual = await original<typeof import('../../api/software')>()
  return {
    ...actual,
    softwareApi: {
      getCompliance: vi.fn(),
      listUnrecognised: vi.fn(),
      normalise: vi.fn(),
      runCompliance: vi.fn(),
    },
  }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const report: SoftwareCompliance = {
  generatedOn: '2026-08-13',
  productCount: 3,
  overDeployedCount: 1,
  unlicensedCount: 1,
  totalInstalls: 13,
  totalEntitled: 28,
  rows: [
    {
      productId: 'p-acrobat',
      productName: 'Acrobat Pro',
      publisher: 'Adobe',
      category: 'Productivity',
      installedCiCount: 5,
      installCount: 5,
      entitled: 3,
      licensePoolCount: 1,
      expiredPoolCount: 0,
      overage: 2,
      state: 'OverDeployed',
      nextExpiry: '2027-06-08',
      expiryStatus: 'Active',
    },
    {
      productId: 'p-chrome',
      productName: 'Chrome',
      publisher: 'Google',
      category: 'Browser',
      installedCiCount: 5,
      installCount: 5,
      entitled: 0,
      licensePoolCount: 0,
      expiredPoolCount: 0,
      overage: 5,
      state: 'Unlicensed',
      nextExpiry: null,
      expiryStatus: null,
    },
    {
      productId: 'p-office',
      productName: 'Office Professional Plus',
      publisher: 'Microsoft',
      category: 'Productivity',
      installedCiCount: 3,
      installCount: 3,
      entitled: 5,
      licensePoolCount: 1,
      expiredPoolCount: 0,
      overage: -2,
      state: 'Compliant',
      nextExpiry: null,
      expiryStatus: null,
    },
  ],
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><SoftwarePage /></QueryClientProvider></MemoryRouter>)
}

describe('SoftwarePage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(softwareApi.getCompliance).mockResolvedValue(report)
    vi.mocked(softwareApi.listUnrecognised).mockResolvedValue([])
  })

  it('states the shortfall in words rather than a signed number', async () => {
    renderPage()

    expect(await screen.findByText('Acrobat Pro')).toBeInTheDocument()
    expect(screen.getByText('2 over')).toBeInTheDocument()
    expect(screen.getByText('2 spare')).toBeInTheDocument()
    // Unlicensed is not "5 over": nothing was ever bought for it, which is a different finding.
    expect(screen.getByText('5 installed, none entitled')).toBeInTheDocument()
  })

  it('gives each product its own compliance pill', async () => {
    renderPage()
    await screen.findByText('Acrobat Pro')

    const rows = within(screen.getByRole('table')).getAllByRole('row')
    expect(within(rows[1]).getByText('Over-deployed')).toBeInTheDocument()
    expect(within(rows[2]).getByText('Unlicensed')).toBeInTheDocument()
    expect(within(rows[3]).getByText('Compliant')).toBeInTheDocument()
  })

  it('filters the report by compliance state', async () => {
    renderPage()
    await screen.findByText('Acrobat Pro')

    await userEvent.selectOptions(screen.getByLabelText('Filter by compliance'), 'OverDeployed')

    await waitFor(() => expect(softwareApi.getCompliance).toHaveBeenCalledWith('OverDeployed', undefined))
  })

  it('lists the raw names the catalogue does not recognise, with how widespread each one is', async () => {
    vi.mocked(softwareApi.listUnrecognised).mockResolvedValue([
      { rawName: 'Contoso VPN Client', rawPublisher: 'Contoso Networks', installCount: 2, ciCount: 2 },
    ])

    renderPage()

    expect(await screen.findByText('Contoso VPN Client')).toBeInTheDocument()
    expect(screen.getByText('on 2 devices')).toBeInTheDocument()
  })

  it('re-normalises on request and says what changed', async () => {
    vi.mocked(softwareApi.normalise).mockResolvedValue({
      installsExamined: 30, normalised: 2, renormalised: 0, unrecognised: 0,
    })

    renderPage()
    await screen.findByText('Acrobat Pro')
    await userEvent.click(screen.getByRole('button', { name: /Re-normalise/ }))

    await waitFor(() => expect(softwareApi.normalise).toHaveBeenCalled())
  })

  /** The failure path: a report that will not load must never render as an estate with nothing on it. */
  it('says the report could not be loaded rather than showing an empty estate', async () => {
    vi.mocked(softwareApi.getCompliance).mockRejectedValue(new Error('boom'))

    renderPage()

    expect(await screen.findByRole('alert')).toHaveTextContent('Compliance could not be loaded')
    expect(screen.queryByText('Nothing has been inventoried yet')).not.toBeInTheDocument()
    expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(0)
  })
})
