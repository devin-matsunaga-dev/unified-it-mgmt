import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci, type CiLifecycleStateInfo, type CiTypeSchema } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import { CiListPage } from './CiListPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, listCis: vi.fn(), listTypeSchemas: vi.fn(), createCi: vi.fn(), updateCi: vi.fn(), listLifecycleStates: vi.fn(), transitionCi: vi.fn(), assignCi: vi.fn(), getLifecycleHistory: vi.fn(), getAssignments: vi.fn(), bulkEditCis: vi.fn() } }
})

vi.mock('../../api/directory', () => ({
  directoryApi: { listUsers: vi.fn(), listDepartments: vi.fn(), listSites: vi.fn() },
}))

const server: Ci = {
  id: 'ci-1', type: 'Server', name: 'app-01', assetTag: 'AT-0001', serialNumber: 'SN-0001', description: null,
  isActive: true, lifecycleState: 'InStock',
  ownership: { ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteId: null, siteName: null, assignedAt: null },
  coverage: { contractId: null, contractName: null, contractNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: { hostname: 'app-01', operatingSystem: 'Ubuntu 24.04', cpuCores: '8', ramGb: '32' },
  customFields: [], createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T01:00:00Z',
}

const schemas: CiTypeSchema[] = [
  {
    type: 'Hardware',
    attributes: [
      { key: 'manufacturer', label: 'Manufacturer', kind: 'Text', isRequired: true },
      { key: 'model', label: 'Model', kind: 'Text', isRequired: true },
    ],
    customFields: [],
  },
  {
    type: 'Server',
    attributes: [
      { key: 'hostname', label: 'Hostname', kind: 'Text', isRequired: true },
      { key: 'cpuCores', label: 'CPU cores', kind: 'Integer', isRequired: true },
    ],
    customFields: [],
  },
]

const lifecycleStates: CiLifecycleStateInfo[] = [
  { state: 'Ordered', allowedTargets: ['InStock'] },
  { state: 'InStock', allowedTargets: ['Deployed', 'InRepair', 'Retired'] },
  { state: 'Deployed', allowedTargets: ['InStock', 'InRepair', 'Retired'] },
  { state: 'InRepair', allowedTargets: ['Deployed', 'InStock', 'Retired'] },
  { state: 'Retired', allowedTargets: ['Disposed'] },
  { state: 'Disposed', allowedTargets: [] },
]

const directoryUsers = [
  { id: 'user-1', username: 'enduser1', displayName: 'End User One', email: 'enduser1@example.test', role: 'EndUser', siteId: 'site-1', siteName: 'Head Office', departmentId: 'dept-1', departmentName: 'Finance' },
]

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><CiListPage /></QueryClientProvider></MemoryRouter>)
}

describe('CiListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue(schemas)
    vi.mocked(assetsApi.listLifecycleStates).mockResolvedValue(lifecycleStates)
    vi.mocked(assetsApi.getLifecycleHistory).mockResolvedValue([])
    vi.mocked(assetsApi.getAssignments).mockResolvedValue([])
    vi.mocked(directoryApi.listUsers).mockResolvedValue(directoryUsers)
    vi.mocked(directoryApi.listDepartments).mockResolvedValue([{ id: 'dept-1', code: 'FIN', name: 'Finance' }])
    vi.mocked(directoryApi.listSites).mockResolvedValue([{ id: 'site-1', code: 'HQ', name: 'Head Office' }])
  })

  it('sends the selected type filter to the API', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    renderPage()
    expect(await screen.findByText('app-01')).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by type' }), 'Server')

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(expect.objectContaining({ type: 'Server' })))
  })

  it('sends the typed search term and shows the empty state when nothing matches', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    renderPage()
    expect(await screen.findByText('app-01')).toBeInTheDocument()

    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })
    await userEvent.type(screen.getByRole('textbox', { name: 'Search configuration items' }), 'switch')

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(expect.objectContaining({ search: 'switch' })))
    expect(await screen.findByText('No matching configuration items')).toBeInTheDocument()
  })

  it('renders the attributes of the chosen type and creates the CI', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })
    vi.mocked(assetsApi.createCi).mockResolvedValue({ ...server, name: 'core-sw-01' })
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'New CI' }))
    const dialog = screen.getByRole('dialog')
    // Hardware is the default type, so its attributes are the ones on screen.
    expect(within(dialog).getByLabelText(/Manufacturer/)).toBeInTheDocument()
    expect(within(dialog).queryByLabelText(/CPU cores/)).not.toBeInTheDocument()

    await userEvent.selectOptions(within(dialog).getByLabelText('Type'), 'Server')
    expect(within(dialog).getByLabelText(/CPU cores/)).toBeInTheDocument()
    expect(within(dialog).queryByLabelText(/Manufacturer/)).not.toBeInTheDocument()

    await userEvent.type(within(dialog).getByLabelText(/Name/), 'core-sw-01')
    await userEvent.type(within(dialog).getByLabelText(/Hostname/), 'core-sw-01')
    await userEvent.type(within(dialog).getByLabelText(/CPU cores/), '4')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create CI' }))

    await waitFor(() => expect(assetsApi.createCi).toHaveBeenCalledWith(expect.objectContaining({
      type: 'Server',
      name: 'core-sw-01',
      attributes: { hostname: 'core-sw-01', cpuCores: '4' },
    })))
  })

  it('blocks a submit that is missing a required type attribute', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'New CI' }))
    const dialog = screen.getByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText(/Name/), 'Laptop with no model')
    await userEvent.type(within(dialog).getByLabelText(/Manufacturer/), 'Dell')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create CI' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Model is required.')
    expect(assetsApi.createCi).not.toHaveBeenCalled()
  })

  it('renders a custom field the admin added at runtime', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue([
      {
        ...schemas[0],
        customFields: [{ id: 'field-1', ciType: 'Hardware', key: 'rack_unit', label: 'Rack unit', type: 'Text', isRequired: true, options: [], sortOrder: 0 }],
      },
      schemas[1],
    ])
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'New CI' }))
    const dialog = screen.getByRole('dialog')

    expect(within(dialog).getByRole('heading', { name: 'Custom fields' })).toBeInTheDocument()
    expect(within(dialog).getByLabelText(/Rack unit/)).toBeInTheDocument()
  })

  it('opens the lifecycle drawer, offers only the legal next states, and transitions', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    vi.mocked(assetsApi.transitionCi).mockResolvedValue({ ...server, lifecycleState: 'Deployed' })
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Lifecycle' }))
    const drawer = await screen.findByRole('dialog', { name: /Lifecycle and ownership for app-01/ })

    // In stock: deploy, repair, and retire are legal; disposal is not reachable without retiring.
    expect(within(drawer).getByRole('button', { name: 'Deployed' })).toBeInTheDocument()
    expect(within(drawer).queryByRole('button', { name: 'Disposed' })).not.toBeInTheDocument()

    await userEvent.click(within(drawer).getByRole('button', { name: 'Deployed' }))

    await waitFor(() => expect(assetsApi.transitionCi).toHaveBeenCalledWith('ci-1', 'Deployed', null))
  })

  it('checks a CI out to a user and prefills that user\'s department and location', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    vi.mocked(assetsApi.assignCi).mockResolvedValue(server)
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Lifecycle' }))
    const drawer = await screen.findByRole('dialog', { name: /Lifecycle and ownership for app-01/ })
    await waitFor(() => expect(within(drawer).getByLabelText('Owner')).toHaveDisplayValue('Unassigned (check in)'))

    await userEvent.selectOptions(within(drawer).getByLabelText('Owner'), 'user-1')
    await userEvent.type(within(drawer).getByLabelText('Note'), 'Onboarding')
    await userEvent.click(within(drawer).getByRole('button', { name: 'Save assignment' }))

    await waitFor(() => expect(assetsApi.assignCi).toHaveBeenCalledWith('ci-1', {
      ownerUserId: 'user-1', departmentId: 'dept-1', siteId: 'site-1', note: 'Onboarding',
    }))
  })

  it('bulk edits the selected rows and clears the selection', async () => {
    const laptop: Ci = { ...server, id: 'ci-2', name: 'laptop-7', type: 'Hardware' }
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server, laptop], total: 2, page: 1, pageSize: 25 })
    vi.mocked(assetsApi.bulkEditCis).mockResolvedValue({ total: 2, succeeded: 2, failed: 0, rows: [] })
    renderPage()

    await userEvent.click(await screen.findByRole('checkbox', { name: 'Select app-01' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select laptop-7' }))
    expect(screen.getByText('2 selected')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Bulk edit' }))
    const dialog = await screen.findByRole('dialog', { name: /Bulk edit 2 configuration items/ })
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Move to a lifecycle state' }))
    await userEvent.selectOptions(within(dialog).getByLabelText('Lifecycle state'), 'Deployed')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Apply to 2 items' }))

    await waitFor(() => expect(assetsApi.bulkEditCis).toHaveBeenCalledWith({
      ciIds: ['ci-1', 'ci-2'], ownership: undefined, lifecycleState: 'Deployed', note: null,
    }))
    await waitFor(() => expect(screen.queryByText('2 selected')).not.toBeInTheDocument())
  })

  it('lists the CIs a bulk edit could not change', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    vi.mocked(assetsApi.bulkEditCis).mockResolvedValue({
      total: 1, succeeded: 0, failed: 1,
      rows: [{ ciId: 'ci-1', name: 'app-01', succeeded: false, error: 'A CI cannot move from Ordered to Deployed.' }],
    })
    renderPage()

    await userEvent.click(await screen.findByRole('checkbox', { name: 'Select app-01' }))
    await userEvent.click(screen.getByRole('button', { name: 'Bulk edit' }))
    const dialog = await screen.findByRole('dialog', { name: /Bulk edit 1 configuration items/ })
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Move to a lifecycle state' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Apply to 1 items' }))

    expect(await within(dialog).findByText(/cannot move from Ordered to Deployed/)).toBeInTheDocument()
  })

  it('refuses to apply a bulk edit that changes nothing', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    renderPage()

    await userEvent.click(await screen.findByRole('checkbox', { name: 'Select app-01' }))
    await userEvent.click(screen.getByRole('button', { name: 'Bulk edit' }))
    const dialog = await screen.findByRole('dialog', { name: /Bulk edit 1 configuration items/ })

    expect(within(dialog).getByRole('button', { name: 'Apply to 1 items' })).toBeDisabled()
    expect(assetsApi.bulkEditCis).not.toHaveBeenCalled()
  })

  it('freezes the drawer for a disposed CI', async () => {
    const disposed: Ci = { ...server, lifecycleState: 'Disposed', isActive: false }
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [disposed], total: 1, page: 1, pageSize: 25 })
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Lifecycle' }))
    const drawer = await screen.findByRole('dialog', { name: /Lifecycle and ownership for app-01/ })

    expect(within(drawer).getByText(/A disposed CI is a closed record/)).toBeInTheDocument()
    expect(within(drawer).getByRole('button', { name: 'Save assignment' })).toBeDisabled()
    expect(within(drawer).getByLabelText('Owner')).toBeDisabled()
  })

  it('sends the owner filter to the API so a user\'s assets can be listed', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    renderPage()
    expect(await screen.findByText('app-01')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Filter by owner' })).toHaveTextContent('End User One'))

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Filter by owner' }), 'user-1')

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(expect.objectContaining({ ownerUserId: 'user-1' })))
  })

  it('offers a retry when loading fails', async () => {
    vi.mocked(assetsApi.listCis).mockRejectedValue(new Error('Service unavailable'))
    renderPage()

    expect(await screen.findByText('Configuration items could not be loaded')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })

  describe('sorting', () => {
    const laptop: Ci = { ...server, id: 'ci-2', name: 'zeta-laptop', type: 'Hardware', lifecycleState: 'Deployed' }
    const rowNames = () => screen.getAllByRole('row').slice(1)
      .map((row) => (row as HTMLTableRowElement).cells[1].textContent)

    it('reorders the rows on screen and marks the sorted column', async () => {
      vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server, laptop], total: 2, page: 1, pageSize: 25 })
      renderPage()
      await screen.findByText('app-01')

      await userEvent.click(screen.getByRole('button', { name: 'Sort by name' }))
      expect(rowNames()).toEqual(['app-01', 'zeta-laptop'])
      expect(screen.getByRole('columnheader', { name: /Name/ })).toHaveAttribute('aria-sort', 'ascending')

      await userEvent.click(screen.getByRole('button', { name: 'Sort by name' }))
      expect(rowNames()).toEqual(['zeta-laptop', 'app-01'])
      expect(screen.getByRole('columnheader', { name: /Name/ })).toHaveAttribute('aria-sort', 'descending')
    })

    it('returns to the server\'s own order on the third click', async () => {
      vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [laptop, server], total: 2, page: 1, pageSize: 25 })
      renderPage()
      await screen.findByText('app-01')

      const header = screen.getByRole('button', { name: 'Sort by name' })
      await userEvent.click(header)
      await userEvent.click(header)
      await userEvent.click(header)

      // The order the API sent, not ascending or descending.
      expect(rowNames()).toEqual(['zeta-laptop', 'app-01'])
      expect(screen.getByRole('columnheader', { name: /Name/ })).toHaveAttribute('aria-sort', 'none')
    })

    // Sorting runs after paging, so on a multi-page estate it cannot see the rows it would reorder.
    it('says so when the sort covers only the page on screen', async () => {
      vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server, laptop], total: 60, page: 1, pageSize: 25 })
      renderPage()
      await screen.findByText('app-01')

      expect(screen.queryByText(/Sorted within this page/)).not.toBeInTheDocument()
      await userEvent.click(screen.getByRole('button', { name: 'Sort by lifecycle' }))
      expect(screen.getByText(/Sorted within this page of 25 — not across all 60/)).toBeInTheDocument()
    })

    it('stays quiet about scope when the whole estate is on one page', async () => {
      vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server, laptop], total: 2, page: 1, pageSize: 25 })
      renderPage()
      await screen.findByText('app-01')

      await userEvent.click(screen.getByRole('button', { name: 'Sort by lifecycle' }))
      expect(screen.queryByText(/Sorted within this page/)).not.toBeInTheDocument()
    })
  })

  describe('estate counts', () => {
    /** Each tile is the list endpoint asked for one row, so a count is whatever its filter totals. */
    const countBy = (totals: { deployed: number; repair: number; warranty: number; all: number }) =>
      vi.mocked(assetsApi.listCis).mockImplementation((filter = {}) => Promise.resolve({
        items: [server], page: 1, pageSize: filter.pageSize ?? 25,
        total: filter.lifecycleState === 'Deployed' ? totals.deployed
          : filter.lifecycleState === 'InRepair' ? totals.repair
          : filter.warrantyExpiringWithinDays === 30 ? totals.warranty
          : totals.all,
      }))

    /** The tiles only; "In repair" and "Deployed" are also lifecycle filter options on this page. */
    const tiles = async () => within(await screen.findByRole('group', { name: 'Estate counts' }))

    it('counts the estate on the same definitions the table filters by', async () => {
      countBy({ all: 60, deployed: 41, repair: 3, warranty: 7 })
      renderPage()

      const stats = await tiles()
      expect(await stats.findByText('60')).toBeInTheDocument()
      expect(stats.getByText('Deployed').closest('button')).toHaveTextContent('41')
      expect(stats.getByText('In repair').closest('button')).toHaveTextContent('3')
      expect(stats.getByText(/Warranty ends within 30 days/).closest('button')).toHaveTextContent('7')
      await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith({ warrantyExpiringWithinDays: 30, page: 1, pageSize: 1 }))
    })

    it('narrows the list to what a tile counts, and clears it when the same tile is pressed again', async () => {
      countBy({ all: 60, deployed: 41, repair: 3, warranty: 7 })
      renderPage()

      const stats = await tiles()
      await userEvent.click(stats.getByText('In repair').closest('button')!)
      await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(expect.objectContaining({ lifecycleState: 'InRepair', page: 1 })))
      expect(stats.getByText('In repair').closest('button')).toHaveAttribute('aria-pressed', 'true')

      await userEvent.click(stats.getByText('In repair').closest('button')!)
      await waitFor(() => expect(screen.getByRole('combobox', { name: 'Filter by lifecycle state' })).toHaveValue(''))
    })

    // A tile that failed to count must not print a zero: nothing distinguishes it from a true zero.
    it('says a count is unavailable rather than showing it as none', async () => {
      vi.mocked(assetsApi.listCis).mockRejectedValue(new Error('Service unavailable'))
      renderPage()

      expect((await (await tiles()).findAllByText('Unavailable')).length).toBe(4)
    })
  })
})
