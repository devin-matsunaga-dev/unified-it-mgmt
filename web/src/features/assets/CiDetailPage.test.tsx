import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci, type CiGraph, type CiRelationships, type CiTypeSchema } from '../../api/assets'
import { helpdeskApi, type Ticket } from '../../api/helpdesk'
import { CiDetailPage } from './CiDetailPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: Object.fromEntries(Object.entries(actual.assetsApi).map(([name, value]) => [name, typeof value === 'function' ? vi.fn() : value])) }
})

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listTickets: vi.fn() } }
})

vi.mock('../../api/directory', () => ({
  directoryApi: { listUsers: vi.fn().mockResolvedValue([]), listDepartments: vi.fn().mockResolvedValue([]), listSites: vi.fn().mockResolvedValue([]) },
}))

const host: Ci = {
  id: 'ci-host', type: 'Server', name: 'esx-01', assetTag: 'AT-9001', serialNumber: 'SN-9001', description: 'Primary hypervisor host',
  isActive: true, lifecycleState: 'Deployed',
  ownership: { ownerUserId: 'user-1', ownerName: 'Technician One', departmentId: 'dept-1', departmentName: 'IT', siteId: 'site-1', siteName: 'Head Office', assignedAt: '2026-08-01T00:00:00Z' },
  coverage: { contractId: null, contractName: null, contractNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: { hostname: 'esx-01', operatingSystem: 'ESXi 8', cpuCores: '32', ramGb: '512' },
  customFields: [], createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-06T00:00:00Z',
}

const schemas: CiTypeSchema[] = [{
  type: 'Server',
  attributes: [
    { key: 'hostname', label: 'Hostname', kind: 'Text', isRequired: true },
    { key: 'operatingSystem', label: 'Operating system', kind: 'Text', isRequired: true },
    { key: 'cpuCores', label: 'CPU cores', kind: 'Integer', isRequired: true },
    { key: 'ramGb', label: 'RAM (GB)', kind: 'Integer', isRequired: true },
  ],
  customFields: [],
}]

const ancestors: CiGraph = {
  rootCiId: host.id, direction: 'Ancestors', maxDepth: 3, maxDepthReached: false, containsCycle: false,
  nodes: [{ id: 'ci-switch', type: 'NetworkDevice', name: 'core-sw-01', assetTag: null, lifecycleState: 'Deployed', isActive: true, depth: 1 }],
  edges: [{ id: 'edge-1', sourceCiId: host.id, targetCiId: 'ci-switch', type: 'ConnectsTo' }],
}

const impact: CiGraph = {
  rootCiId: host.id, direction: 'Descendants', maxDepth: 3, maxDepthReached: false, containsCycle: false,
  nodes: [
    { id: host.id, type: 'Server', name: 'esx-01', assetTag: 'AT-9001', lifecycleState: 'Deployed', isActive: true, depth: 0 },
    { id: 'ci-vm', type: 'Virtual', name: 'vm-payroll', assetTag: null, lifecycleState: 'Deployed', isActive: true, depth: 1 },
  ],
  edges: [{ id: 'edge-2', sourceCiId: 'ci-vm', targetCiId: host.id, type: 'RunsOn' }],
}

// The same two edges the graphs above are built from, as the direct relationships the card lists.
const relationships: CiRelationships = {
  ciId: host.id,
  upstream: [{ id: 'edge-1', sourceCiId: host.id, sourceCiName: 'esx-01', sourceCiType: 'Server', targetCiId: 'ci-switch', targetCiName: 'core-sw-01', targetCiType: 'NetworkDevice', type: 'ConnectsTo', description: null, createdBy: 'technician1', createdAt: '2026-08-03T00:00:00Z' }],
  downstream: [{ id: 'edge-2', sourceCiId: 'ci-vm', sourceCiName: 'vm-payroll', sourceCiType: 'Virtual', targetCiId: host.id, targetCiName: 'esx-01', targetCiType: 'Server', type: 'RunsOn', description: null, createdBy: 'technician1', createdAt: '2026-08-04T00:00:00Z' }],
}

const ticket: Ticket = {
  id: 'ticket-1', number: 'INC-000042', title: 'Host fan failure', description: 'Loud', type: 'Incident',
  urgency: 'High', impact: 'High', priority: 'Critical', status: 'InProgress', requesterId: 'enduser1',
  requesterName: 'End User One', queueId: null, queueName: null, assignedTechnicianId: 'technician1',
  createdAt: '2026-08-05T00:00:00Z', updatedAt: '2026-08-06T00:00:00Z', categoryId: null, categoryName: null, customFields: [],
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={[`/assets/${host.id}`]}><QueryClientProvider client={client}>
    <Routes><Route path="/assets/:id" element={<CiDetailPage />} /></Routes>
  </QueryClientProvider></MemoryRouter>)
}

describe('CiDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.getCi).mockResolvedValue(host)
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue(schemas)
    vi.mocked(assetsApi.listLifecycleStates).mockResolvedValue([{ state: 'Deployed', allowedTargets: ['InStock', 'InRepair', 'Retired'] }])
    vi.mocked(assetsApi.getAncestors).mockResolvedValue(ancestors)
    vi.mocked(assetsApi.getImpactedBy).mockResolvedValue(impact)
    vi.mocked(assetsApi.getRelationships).mockResolvedValue(relationships)
    vi.mocked(assetsApi.getLifecycleHistory).mockResolvedValue([{ id: 'history-1', ciId: host.id, fromState: 'InStock', toState: 'Deployed', note: 'Racked', actorId: 'technician1', occurredAt: '2026-08-02T00:00:00Z' }])
    vi.mocked(assetsApi.getAssignments).mockResolvedValue([])
    vi.mocked(helpdeskApi.listTickets).mockResolvedValue({ items: [ticket], total: 1, page: 1, pageSize: 200 })
  })

  it('shows the CI, its attributes, its owner, and its lifecycle history', async () => {
    renderPage()

    expect(await screen.findByRole('heading', { name: 'esx-01' })).toBeInTheDocument()
    expect(screen.getByText('Hostname').parentElement).toHaveTextContent('esx-01')
    expect(screen.getByText('RAM (GB)').parentElement).toHaveTextContent('512')
    expect(screen.getByRole('link', { name: 'Technician One' })).toHaveAttribute('href', '/people/user-1')
    expect(screen.getByText('In stock → Deployed')).toBeInTheDocument()
  })

  it('asks the API only for the tickets linked to this CI and lists them', async () => {
    renderPage()

    await waitFor(() => expect(helpdeskApi.listTickets).toHaveBeenCalledWith({ ciId: host.id }))
    const tickets = (await screen.findByRole('heading', { name: 'Ticket history' })).closest('section')!
    expect(within(tickets).getByRole('link', { name: 'Host fan failure' })).toHaveAttribute('href', '/tickets/ticket-1')
    expect(within(tickets).getByText('#INC-000042')).toBeInTheDocument()
  })

  it('draws the mini-graph with what the CI needs above and what needs it below', async () => {
    renderPage()

    const relations = (await screen.findByRole('heading', { name: 'Relations' })).closest('section')!
    // Scoped to the bands: the editable edge list above them links the same two CIs by name.
    const graph = await within(relations).findByLabelText('Dependency graph')
    // The impacted-by walk includes the CI itself at depth 0; the centre band already shows it.
    await waitFor(() => expect(within(graph).getByRole('link', { name: /core-sw-01/ })).toHaveAttribute('href', '/assets/ci-switch'))
    expect(within(graph).getByRole('link', { name: /vm-payroll/ })).toHaveAttribute('href', '/assets/ci-vm')
    expect(within(graph).queryByRole('link', { name: /esx-01/ })).not.toBeInTheDocument()
    expect(within(relations).getByText('2 relationships')).toBeInTheDocument()
  })

  it('warns when the graph is truncated or cyclic', async () => {
    vi.mocked(assetsApi.getImpactedBy).mockResolvedValue({ ...impact, containsCycle: true, maxDepthReached: true })
    renderPage()

    const relations = (await screen.findByRole('heading', { name: 'Relations' })).closest('section')!
    expect(await within(relations).findByText(/contains a cycle/)).toBeInTheDocument()
    expect(within(relations).getByText(/Stops at 3 hops/)).toBeInTheDocument()
  })

  it('offers a retry when the CI cannot be loaded', async () => {
    vi.mocked(assetsApi.getCi).mockRejectedValue(new Error('CI not found.'))
    renderPage()

    expect(await screen.findByText('Configuration item could not be loaded')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
})
