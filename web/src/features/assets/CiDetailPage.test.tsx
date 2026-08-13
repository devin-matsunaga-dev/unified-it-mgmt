import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, discoveryApi, type Ci, type CiDiscoveryFacts, type CiGraph, type CiRelationships, type CiTypeSchema } from '../../api/assets'
import { ApiError } from '../../api/client'
import { helpdeskApi, type Ticket } from '../../api/helpdesk'
import { formatDateOnly } from '../../lib/utils'
import { CiDetailPage } from './CiDetailPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return {
    ...actual,
    assetsApi: Object.fromEntries(Object.entries(actual.assetsApi).map(([name, value]) => [name, typeof value === 'function' ? vi.fn() : value])),
    discoveryApi: { ...actual.discoveryApi, getCiDiscoveryFacts: vi.fn() },
  }
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

const discoveryFacts: CiDiscoveryFacts = {
  ciId: host.id,
  address: '172.18.0.9',
  hostname: 'esx-01.corp.example',
  respondedToPing: true,
  openPorts: [22, 443],
  snmp: {
    sysName: 'esx-01', sysDescription: 'VMware ESXi 8.0.2 build-23305546',
    sysObjectId: '1.3.6.1.4.1.6876', sysLocation: 'Head Office', sysContact: null, uptimeSeconds: 864000,
  },
  neighbours: [],
  discoveryName: 'discovery-1',
  scanProfileName: 'Local subnet sweep',
  firstSeenAt: '2026-08-10T00:00:00Z',
  lastSeenAt: '2026-08-13T09:00:00Z',
  sightingCount: 7,
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
    vi.mocked(discoveryApi.getCiDiscoveryFacts).mockResolvedValue(discoveryFacts)
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

  it('draws a path in each direction, rooted at the open CI', async () => {
    renderPage()

    const relations = (await screen.findByRole('heading', { name: 'Relations' })).closest('section')!
    // Scoped to each tree: the editable edge cards above them link the same two CIs by name.
    const upstream = within(await within(relations).findByRole('list', { name: 'Dependency path' }))
    await waitFor(() => expect(upstream.getByRole('link', { name: 'core-sw-01' })).toHaveAttribute('href', '/assets/ci-switch'))

    const downstream = within(within(relations).getByRole('list', { name: 'Downstream impact' }))
    expect(downstream.getByRole('link', { name: 'vm-payroll' })).toHaveAttribute('href', '/assets/ci-vm')

    // The root is the CI itself, so it is labelled rather than linked in either tree.
    expect(upstream.queryByRole('link', { name: 'esx-01' })).not.toBeInTheDocument()
    expect(downstream.queryByRole('link', { name: 'esx-01' })).not.toBeInTheDocument()
    expect(within(relations).getByText('2 direct relationships')).toBeInTheDocument()
  })

  it('says a graph is cyclic, and offers to walk further when the depth ceiling truncated it', async () => {
    vi.mocked(assetsApi.getImpactedBy).mockResolvedValue({ ...impact, containsCycle: true, maxDepthReached: true })
    renderPage()

    const relations = (await screen.findByRole('heading', { name: 'Relations' })).closest('section')!
    expect(await within(relations).findByText(/contains a cycle/)).toBeInTheDocument()
    // A truncated walk is no longer a dead end — the reader can ask for the rest.
    expect(within(relations).getByRole('button', { name: /Show deeper/ })).toBeInTheDocument()
  })

  // Both cards used to answer any outcome with their empty sentence, which asserts a fact about the
  // asset when what actually happened is that the request failed.
  it('distinguishes a history that is empty from one that could not be read', async () => {
    vi.mocked(assetsApi.getLifecycleHistory).mockRejectedValue(new Error('Service unavailable'))
    vi.mocked(assetsApi.getAssignments).mockResolvedValue([])
    renderPage()

    const lifecycle = (await screen.findByRole('heading', { name: 'Lifecycle history' })).closest('section')!
    expect(await within(lifecycle).findByRole('alert')).toHaveTextContent('The lifecycle history could not be loaded.')
    expect(within(lifecycle).queryByText(/no transitions yet/)).not.toBeInTheDocument()

    // The other card is unaffected and still states its own emptiness.
    const log = screen.getByRole('heading', { name: 'Check-in / out log' }).closest('section')!
    expect(within(log).getByText('Nobody has held this asset yet.')).toBeInTheDocument()
  })

  it('says the check-in log could not be read when its request fails', async () => {
    vi.mocked(assetsApi.getAssignments).mockRejectedValue(new Error('Service unavailable'))
    renderPage()

    const log = (await screen.findByRole('heading', { name: 'Check-in / out log' })).closest('section')!
    expect(await within(log).findByRole('alert')).toHaveTextContent('The check-in / out log could not be loaded.')
  })

  // WP-2.6 stores these as DateOnly, so they must be stated as calendar days and never shifted by a
  // timezone — but they must also stop being the only raw ISO strings on a page of formatted dates.
  it('writes the coverage dates as calendar dates rather than raw ISO strings', async () => {
    vi.mocked(assetsApi.getCi).mockResolvedValue({
      ...host,
      coverage: { ...host.coverage, purchaseDate: '2026-01-05', warrantyExpiresAt: '2026-09-14', warrantyStatus: 'Active', warrantyDaysRemaining: 36 },
    })
    renderPage()

    const coverage = (await screen.findByRole('heading', { name: /Warranty/ })).closest('section')!
    expect(within(coverage).queryByText('2026-09-14')).not.toBeInTheDocument()
    expect(within(coverage).getByText(formatDateOnly('2026-09-14'))).toBeInTheDocument()
    expect(within(coverage).getByText(formatDateOnly('2026-01-05'))).toBeInTheDocument()
  })

  it('offers a retry when the CI cannot be loaded', async () => {
    vi.mocked(assetsApi.getCi).mockRejectedValue(new Error('CI not found.'))
    renderPage()

    expect(await screen.findByText('Configuration item could not be loaded')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })

  it('shows what the network last said about the CI, beside what the CMDB records', async () => {
    renderPage()

    const discovery = (await screen.findByRole('heading', { name: 'Discovery' })).closest('section')!
    // The scanned values sit here; the recorded attributes above the fold are untouched by them, which
    // is the difference WP-4.6's drift report is built to find.
    expect(await within(discovery).findByText('VMware ESXi 8.0.2 build-23305546')).toBeInTheDocument()
    expect(within(discovery).getByText('172.18.0.9')).toBeInTheDocument()
    expect(within(discovery).getByText(/7 scans/)).toBeInTheDocument()
    expect(screen.getByText('Operating system').parentElement).toHaveTextContent('ESXi 8')
  })

  it('says plainly that no scan has reached a CI rather than showing an error', async () => {
    vi.mocked(discoveryApi.getCiDiscoveryFacts).mockRejectedValue(new ApiError(404, 'No scan has reported this CI.'))

    renderPage()

    const discovery = (await screen.findByRole('heading', { name: 'Discovery' })).closest('section')!
    expect(await within(discovery).findByText(/No scan has reported this asset/)).toBeInTheDocument()
    expect(within(discovery).queryByRole('alert')).not.toBeInTheDocument()
  })
})
