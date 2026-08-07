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
  return { ...actual, assetsApi: { ...actual.assetsApi, listCis: vi.fn(), listTypeSchemas: vi.fn(), createCi: vi.fn(), updateCi: vi.fn(), listLifecycleStates: vi.fn(), transitionCi: vi.fn(), assignCi: vi.fn(), getLifecycleHistory: vi.fn(), getAssignments: vi.fn() } }
})

vi.mock('../../api/directory', () => ({
  directoryApi: { listUsers: vi.fn(), listDepartments: vi.fn(), listSites: vi.fn() },
}))

const server: Ci = {
  id: 'ci-1', type: 'Server', name: 'app-01', assetTag: 'AT-0001', serialNumber: 'SN-0001', description: null,
  isActive: true, lifecycleState: 'InStock',
  ownership: { ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteId: null, siteName: null, assignedAt: null },
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
})
