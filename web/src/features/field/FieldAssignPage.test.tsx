import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci } from '../../api/assets'
import { directoryApi, type DirectoryUser } from '../../api/directory'
import { FieldAssignPage, actionLabel } from './FieldAssignPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, getCi: vi.fn(), assignCi: vi.fn() } }
})
vi.mock('../../api/directory', async (original) => {
  const actual = await original<typeof import('../../api/directory')>()
  return {
    ...actual,
    directoryApi: { ...actual.directoryApi, listUsers: vi.fn(), listDepartments: vi.fn(), listSites: vi.fn() },
  }
})

const user = (id: string, displayName: string): DirectoryUser => ({
  id, username: id, displayName, email: `${id}@example.test`, role: 'EndUser',
  siteId: 's-1', siteName: 'Riverside', departmentId: 'd-1', departmentName: 'Support',
})

const laptop = (over: Partial<Ci['ownership']> = {}): Ci => ({
  id: 'ci-1', type: 'Hardware', name: 'Reception laptop', assetTag: 'LT-00421', serialNumber: null,
  description: null, isActive: true, lifecycleState: 'Deployed',
  ownership: {
    ownerUserId: null, ownerName: null, departmentId: 'd-1', departmentName: 'Support',
    siteId: 's-1', siteName: 'Riverside', assignedAt: null, ...over,
  },
  coverage: { contractId: null, contractName: null, contractNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: {}, customFields: [], createdAt: '2026-08-08T00:00:00Z', updatedAt: '2026-08-08T00:00:00Z',
})

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/ci/ci-1/assign']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/ci/:id/assign" element={<FieldAssignPage />} />
        <Route path="/field/ci/:id" element={<h1>Field asset page</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('actionLabel', () => {
  it('names each of the four moves the server classifies', () => {
    expect(actionLabel(null, 'u-1')).toBe('Check out')
    expect(actionLabel('u-1', null)).toBe('Check in')
    expect(actionLabel('u-1', 'u-2')).toBe('Transfer')
    expect(actionLabel('u-1', 'u-1')).toBe('Move it')
    expect(actionLabel(null, null)).toBe('Move it')
  })
})

describe('FieldAssignPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop())
    vi.mocked(directoryApi.listUsers).mockResolvedValue([user('u-1', 'Dana Reid'), user('u-2', 'Sam Okafor')])
    vi.mocked(directoryApi.listDepartments).mockResolvedValue([{ id: 'd-1', code: 'SUP', name: 'Support' }])
    vi.mocked(directoryApi.listSites).mockResolvedValue([
      { id: 's-1', code: 'RIV', name: 'Riverside' },
      { id: 's-2', code: 'HAR', name: 'Harbour' },
    ])
  })

  /**
   * The trap this screen exists to avoid: PUT /assignment replaces owner, department and site with
   * exactly what it is sent, so a check-out that omits the two unchanged fields clears the location.
   */
  it('sends the untouched department and site back with a check-out', async () => {
    vi.mocked(assetsApi.assignCi).mockResolvedValue(laptop({ ownerUserId: 'u-1', ownerName: 'Dana Reid' }))

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: /Dana Reid/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Check out' }))

    await waitFor(() => expect(assetsApi.assignCi).toHaveBeenCalledWith('ci-1', {
      ownerUserId: 'u-1', departmentId: 'd-1', siteId: 's-1', note: null,
    }))
  })

  it('checks an asset back into stock without touching where it lives', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop({ ownerUserId: 'u-1', ownerName: 'Dana Reid' }))
    vi.mocked(assetsApi.assignCi).mockResolvedValue(laptop())

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: /back into stock/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Check in' }))

    await waitFor(() => expect(assetsApi.assignCi).toHaveBeenCalledWith('ci-1', {
      ownerUserId: null, departmentId: 'd-1', siteId: 's-1', note: null,
    }))
  })

  it('moves an asset between sites while its holder stays put', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue(laptop({ ownerUserId: 'u-1', ownerName: 'Dana Reid' }))
    vi.mocked(assetsApi.assignCi).mockResolvedValue(laptop({ ownerUserId: 'u-1', ownerName: 'Dana Reid' }))

    renderPage()
    await screen.findByRole('button', { name: /Dana Reid/ })
    await userEvent.selectOptions(screen.getByLabelText('Site'), 's-2')
    await userEvent.click(screen.getByRole('button', { name: 'Move it' }))

    await waitFor(() => expect(assetsApi.assignCi).toHaveBeenCalledWith('ci-1', {
      ownerUserId: 'u-1', departmentId: 'd-1', siteId: 's-2', note: null,
    }))
  })

  it('will not save an untouched form, which would be a no-op write', async () => {
    renderPage()

    await screen.findByRole('button', { name: /Dana Reid/ })
    expect(screen.getByRole('button', { name: 'Move it' })).toBeDisabled()
  })

  it('filters the directory on the phone, since the whole of it arrives at once', async () => {
    renderPage()

    await screen.findByRole('button', { name: /Dana Reid/ })
    await userEvent.type(screen.getByLabelText('Search people'), 'okaf')

    expect(screen.getByRole('button', { name: /Sam Okafor/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Dana Reid/ })).not.toBeInTheDocument()
  })

  it('says so when nobody matches rather than showing an empty list', async () => {
    renderPage()

    await screen.findByRole('button', { name: /Dana Reid/ })
    await userEvent.type(screen.getByLabelText('Search people'), 'zzz')

    expect(screen.getByText('Nobody matches that.')).toBeInTheDocument()
  })
})
