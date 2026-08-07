import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci, type CiTypeSchema } from '../../api/assets'
import { CiListPage } from './CiListPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, listCis: vi.fn(), listTypeSchemas: vi.fn(), createCi: vi.fn(), updateCi: vi.fn() } }
})

const server: Ci = {
  id: 'ci-1', type: 'Server', name: 'app-01', assetTag: 'AT-0001', serialNumber: 'SN-0001', description: null,
  isActive: true, attributes: { hostname: 'app-01', operatingSystem: 'Ubuntu 24.04', cpuCores: '8', ramGb: '32' },
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

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><CiListPage /></QueryClientProvider></MemoryRouter>)
}

describe('CiListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue(schemas)
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

  it('offers a retry when loading fails', async () => {
    vi.mocked(assetsApi.listCis).mockRejectedValue(new Error('Service unavailable'))
    renderPage()

    expect(await screen.findByText('Configuration items could not be loaded')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
})
