import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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
  coverage: { contractId: null, contractName: null, poNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: { hostname: 'app-01', operatingSystem: 'Ubuntu 24.04', cpuCores: '8', ramGb: '32' },
  customFields: [], createdAt: '2026-08-07T00:00:00Z', updatedAt: '2026-08-07T01:00:00Z',
}

const schemas: CiTypeSchema[] = [
  {
    type: 'Hardware',
    attributes: [
      { key: 'manufacturer', label: 'Manufacturer', kind: 'Text', isRequired: true, allowedValues: [] },
      { key: 'model', label: 'Model', kind: 'Text', isRequired: true, allowedValues: [] },
    ],
    customFields: [],
  },
  {
    type: 'Server',
    attributes: [
      { key: 'hostname', label: 'Hostname', kind: 'Text', isRequired: true, allowedValues: [] },
      { key: 'cpuCores', label: 'CPU cores', kind: 'Integer', isRequired: true, allowedValues: [] },
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
    localStorage.clear()
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

  /**
   * The owner filter is a combobox rather than a select: it is the one filter whose list grows with
   * the organisation, so it has to be typeable as well as choosable.
   */
  it('filters by an owner typed into the box', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    expect(await screen.findByText('app-01')).toBeInTheDocument()

    const box = screen.getByRole('combobox', { name: 'Filter by owner' })
    // Matching is on any part of the name, not a prefix — people search by surname.
    await user.type(box, 'user one')
    await user.click(await screen.findByRole('option', { name: 'End User One' }))

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(expect.objectContaining({ ownerUserId: 'user-1' })))
    expect(box).toHaveValue('End User One')
  })

  /** Only the name is shown, and only the name is matched — a hidden match has no visible reason. */
  it('shows only the name in the owner list', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    await user.click(screen.getByRole('combobox', { name: 'Filter by owner' }))

    const option = await screen.findByRole('option', { name: 'End User One' })
    expect(option).toHaveTextContent('End User One')
    expect(option).not.toHaveTextContent('Finance')
  })

  it('does not match on a department that is not shown', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    await user.type(screen.getByRole('combobox', { name: 'Filter by owner' }), 'Finance')

    expect(await screen.findByText(/Nothing matches/)).toBeInTheDocument()
  })

  it('says so when nothing matches what was typed, instead of showing an empty list', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    await user.type(screen.getByRole('combobox', { name: 'Filter by owner' }), 'nobody')

    expect(await screen.findByText(/Nothing matches/)).toBeInTheDocument()
  })

  /** A filter that cannot be cleared is a trap, so "All owners" is always offered. */
  it('clears the owner filter back to all owners', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    const box = screen.getByRole('combobox', { name: 'Filter by owner' })
    await user.click(box)
    await user.click(await screen.findByRole('option', { name: 'End User One' }))
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(expect.objectContaining({ ownerUserId: 'user-1' })))

    await user.click(screen.getByRole('button', { name: 'Clear Filter by owner' }))

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenLastCalledWith(
      expect.objectContaining({ ownerUserId: undefined })))
    expect(box).toHaveValue('')
  })

  /** Keyboard parity with the global search box, which is the other combobox in this app. */
  it('can be driven from the keyboard', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    const box = screen.getByRole('combobox', { name: 'Filter by owner' })
    await user.click(box)
    // Down once lands on "All owners", twice on the first person.
    await user.keyboard('{ArrowDown}{ArrowDown}{Enter}')

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(expect.objectContaining({ ownerUserId: 'user-1' })))
  })

  /** Escape must abandon the interaction without changing the filter. */
  it('closes on escape without changing the filter', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')
    const callsBefore = vi.mocked(assetsApi.listCis).mock.calls.length

    const box = screen.getByRole('combobox', { name: 'Filter by owner' })
    await user.type(box, 'End')
    await user.keyboard('{Escape}')

    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(vi.mocked(assetsApi.listCis).mock.calls.length).toBe(callsBefore)
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

  /**
   * §The sub-filter: CiType stops at "Hardware", so the Select field an admin defined is what says
   * laptop or printer. It appears only once a type is chosen, because a field belongs to a type.
   */
  it('reveals a sub-filter for each choose-one field once a type is picked', async () => {
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue([
      {
        ...schemas[0],
        customFields: [
          {
            id: 'field-kind', ciType: 'Hardware', key: 'hardware_type', label: 'Hardware type',
            type: 'Select', isRequired: false, options: ['Laptop', 'Desktop', 'Printer'], sortOrder: 0,
          },
          {
            id: 'field-po', ciType: 'Hardware', key: 'purchase_order', label: 'Purchase order',
            type: 'Text', isRequired: false, options: [], sortOrder: 1,
          },
        ],
      },
      schemas[1],
    ])
    const user = userEvent.setup()
    renderPage()
    await screen.findByLabelText('Filter by type')

    // Nothing until a type is chosen — "All types" has no fields of its own.
    expect(screen.queryByLabelText('Filter by Hardware type')).not.toBeInTheDocument()

    await user.selectOptions(screen.getByLabelText('Filter by type'), 'Hardware')

    const sub = await screen.findByLabelText('Filter by Hardware type')
    expect([...sub.querySelectorAll('option')].map((option) => option.textContent))
      .toEqual(['All hardware type', 'Laptop', 'Desktop', 'Printer'])
    // A Text field is not a sub-filter: there is nothing to choose from.
    expect(screen.queryByLabelText('Filter by Purchase order')).not.toBeInTheDocument()

    await user.selectOptions(sub, 'Laptop')

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenLastCalledWith(
      expect.objectContaining({ customFields: [{ fieldId: 'field-kind', value: 'Laptop' }] })))
  })

  /**
   * The sub-filters belong to the type being left. Carrying them over would narrow the new type by a
   * field it does not have and quietly return nothing.
   */
  it('drops the sub-filter when the type changes', async () => {
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue([
      {
        ...schemas[0],
        customFields: [{
          id: 'field-kind', ciType: 'Hardware', key: 'hardware_type', label: 'Hardware type',
          type: 'Select', isRequired: false, options: ['Laptop'], sortOrder: 0,
        }],
      },
      schemas[1],
    ])
    const user = userEvent.setup()
    renderPage()
    await screen.findByLabelText('Filter by type')

    await user.selectOptions(screen.getByLabelText('Filter by type'), 'Hardware')
    await user.selectOptions(await screen.findByLabelText('Filter by Hardware type'), 'Laptop')
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenLastCalledWith(
      expect.objectContaining({ customFields: [{ fieldId: 'field-kind', value: 'Laptop' }] })))

    await user.selectOptions(screen.getByLabelText('Filter by type'), 'Server')

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenLastCalledWith(
      expect.objectContaining({ type: 'Server', customFields: undefined })))
    expect(screen.queryByLabelText('Filter by Hardware type')).not.toBeInTheDocument()
  })

  /** Columns are one definition now, so hiding one must remove its header and its cells together. */
  it('hides a column from the header and the rows together', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    expect(screen.getByRole('columnheader', { name: /Serial/ })).toBeInTheDocument()
    expect(screen.getByText('SN-0001')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Columns' }))
    await user.click(screen.getByRole('checkbox', { name: 'Serial' }))

    expect(screen.queryByRole('columnheader', { name: /Serial/ })).not.toBeInTheDocument()
    expect(screen.queryByText('SN-0001')).not.toBeInTheDocument()
  })

  it('remembers the arrangement across a remount', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    const first = renderPage()
    await screen.findByText('app-01')

    await user.click(screen.getByRole('button', { name: 'Columns' }))
    await user.click(screen.getByRole('checkbox', { name: 'Serial' }))
    first.unmount()

    renderPage()
    await screen.findByText('app-01')

    expect(screen.queryByRole('columnheader', { name: /Serial/ })).not.toBeInTheDocument()
  })

  /** An empty table is not a view of anything, and the menu would have no table beside it. */
  it('will not let the last column be hidden', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    await user.click(screen.getByRole('button', { name: 'Columns' }))
    for (const label of ['Type', 'Asset tag', 'Serial', 'Lifecycle', 'Owner', 'Department', 'Location', 'State']) {
      await user.click(screen.getByRole('checkbox', { name: label }))
    }

    const last = screen.getByRole('checkbox', { name: 'Name' })
    expect(last).toBeChecked()
    expect(last).toBeDisabled()
    expect(screen.getByRole('columnheader', { name: /Name/ })).toBeInTheDocument()
  })

  /** Dragging a heading onto another moves it there, header and cells together. */
  it('reorders columns when a heading is dropped on another', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    renderPage()
    await screen.findByText('app-01')

    const headerText = () => screen.getAllByRole('columnheader').map((cell) => cell.textContent?.trim())
    expect(headerText()[1]).toContain('Name')
    expect(headerText()[2]).toContain('Type')

    const location = screen.getByRole('columnheader', { name: /Location/ })
    const name = screen.getByRole('columnheader', { name: /Name/ })
    fireEvent.dragStart(location)
    fireEvent.dragOver(name)
    fireEvent.drop(name)

    // Location has taken Name's place, and Name has shifted along rather than being overwritten.
    expect(headerText()[1]).toContain('Location')
    expect(headerText()[2]).toContain('Name')
  })

  it('hides a filter control that is turned off in the filters menu', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    expect(screen.getByLabelText('Filter by lifecycle state')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Filters' }))
    await user.click(screen.getByRole('checkbox', { name: 'Lifecycle state' }))

    expect(screen.queryByLabelText('Filter by lifecycle state')).not.toBeInTheDocument()
    // Search stays: it is the way into the list, not a narrowing of it.
    expect(screen.getByRole('textbox', { name: 'Search configuration items' })).toBeInTheDocument()
  })

  /**
   * The worst outcome would be a list still narrowed by a control nobody can see: a subset with no
   * visible reason, and no way to widen it without finding the menu again.
   */
  it('clears what a filter was narrowing by when that filter is hidden', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    await userEvent.selectOptions(screen.getByLabelText('Filter by lifecycle state'), 'Deployed')
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      expect.objectContaining({ lifecycleState: 'Deployed' })))

    await user.click(screen.getByRole('button', { name: 'Filters' }))
    await user.click(screen.getByRole('checkbox', { name: 'Lifecycle state' }))

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenLastCalledWith(
      expect.objectContaining({ lifecycleState: undefined })))
  })

  /** The sub-filters belong to Type, so hiding it must take them and their narrowing with it. */
  it('takes the sub-filters with the type control when it is hidden', async () => {
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue([
      {
        ...schemas[0],
        customFields: [{
          id: 'field-kind', ciType: 'Hardware', key: 'hardware_type', label: 'Hardware type',
          type: 'Select', isRequired: false, options: ['Laptop'], sortOrder: 0,
        }],
      },
      schemas[1],
    ])
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    await user.selectOptions(screen.getByLabelText('Filter by type'), 'Hardware')
    await user.selectOptions(await screen.findByLabelText('Filter by Hardware type'), 'Laptop')
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenLastCalledWith(
      expect.objectContaining({ customFields: [{ fieldId: 'field-kind', value: 'Laptop' }] })))

    await user.click(screen.getByRole('button', { name: 'Filters' }))
    await user.click(screen.getByRole('checkbox', { name: 'Type' }))

    expect(screen.queryByLabelText('Filter by type')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Filter by Hardware type')).not.toBeInTheDocument()
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenLastCalledWith(
      expect.objectContaining({ type: undefined, customFields: undefined })))
  })

  it('remembers which filters are shown across a remount', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    const first = renderPage()
    await screen.findByText('app-01')

    await user.click(screen.getByRole('button', { name: 'Filters' }))
    await user.click(screen.getByRole('checkbox', { name: 'Owner' }))
    first.unmount()

    renderPage()
    await screen.findByText('app-01')

    expect(screen.queryByRole('combobox', { name: 'Filter by owner' })).not.toBeInTheDocument()
  })

  /** Columns and filters are separate preferences; turning one off must not disturb the other. */
  it('keeps the column layout and the filter layout apart', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    await user.click(screen.getByRole('button', { name: 'Filters' }))
    await user.click(screen.getByRole('checkbox', { name: 'Owner' }))

    // The Owner column is untouched by hiding the Owner filter.
    expect(screen.getByRole('columnheader', { name: /Owner/ })).toBeInTheDocument()
  })

  /**
   * REGRESSION: applying a pinned tile and then a built-in used to leave the pinned tile's
   * constraints behind, because the handler cleared only the two keys the built-ins happened to
   * use. The built-in then counted one thing while the table showed another.
   */
  it('a built-in tile clears everything a pinned tile was narrowing by', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify([{
      id: 't1',
      label: 'Retired hardware',
      filter: { type: 'Hardware', lifecycleState: 'Retired', isActive: false },
    }]))
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    const tiles = () => within(screen.getByRole('group', { name: 'Estate counts' }))
    await user.click((await tiles().findByText('Retired hardware')).closest('button')!)
    // pageSize 25 is the table's own query; the tiles read pageSize 1 for their counts.
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      expect.objectContaining({ type: 'Hardware', lifecycleState: 'Retired', isActive: false, pageSize: 25 })))

    await user.click(tiles().getByText('Deployed').closest('button')!)

    // Nothing of the pinned tile survives: the list is exactly what the built-in counts.
    // Matched exactly rather than loosely: the whole point is that nothing survived.
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      { lifecycleState: 'Deployed', page: 1, pageSize: 25 }))
  })

  /** The same in the other direction, and for the sub-filters a pinned tile can carry. */
  it('a pinned tile clears what a previous one was narrowing by', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify([
      { id: 't1', label: 'Laptops', filter: { type: 'Hardware', customFields: [{ fieldId: 'f1', value: 'Laptop' }] } },
      { id: 't2', label: 'Owned by one', filter: { ownerUserId: 'user-1' } },
    ]))
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    const tiles = () => within(screen.getByRole('group', { name: 'Estate counts' }))
    await user.click((await tiles().findByText('Laptops')).closest('button')!)
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      expect.objectContaining({ customFields: [{ fieldId: 'f1', value: 'Laptop' }], pageSize: 25 })))

    await user.click(tiles().getByText('Owned by one').closest('button')!)

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      { ownerUserId: 'user-1', page: 1, pageSize: 25 }))
  })

  /**
   * A tile's count is taken without the search term, so a search surviving the click would show
   * fewer rows than the number on the tile promises.
   */
  it('clears the search box when a tile is applied', async () => {
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [server], total: 1, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('app-01')

    const box = screen.getByRole('textbox', { name: 'Search configuration items' })
    await user.type(box, 'switch')
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'switch' })))

    await user.click(within(screen.getByRole('group', { name: 'Estate counts' }))
      .getByText('Deployed').closest('button')!)

    expect(box).toHaveValue('')
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      { lifecycleState: 'Deployed', page: 1, pageSize: 25 }))
  })
})
