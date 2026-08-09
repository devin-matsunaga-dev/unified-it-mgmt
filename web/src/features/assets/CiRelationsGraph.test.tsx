import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci, type CiGraph, type CiRelationships } from '../../api/assets'
import { ApiError } from '../../api/client'
import { CiRelationsGraph } from './CiRelationsGraph'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: Object.fromEntries(Object.entries(actual.assetsApi).map(([name, value]) => [name, typeof value === 'function' ? vi.fn() : value])) }
})

const toasts = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn() }))
vi.mock('sonner', () => ({ toast: toasts }))

const host: Ci = {
  id: 'ci-host', type: 'Server', name: 'esx-01', assetTag: 'AT-9001', serialNumber: 'SN-9001', description: null,
  isActive: true, lifecycleState: 'Deployed',
  ownership: { ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteId: null, siteName: null, assignedAt: null },
  coverage: { contractId: null, contractName: null, contractNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: {}, customFields: [], createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-06T00:00:00Z',
}
const switchCi: Ci = { ...host, id: 'ci-switch', name: 'core-sw-01', type: 'NetworkDevice', assetTag: 'AT-7001' }
const vm: Ci = { ...host, id: 'ci-vm', name: 'vm-payroll', type: 'Virtual', assetTag: null }

const emptyGraph = (direction: CiGraph['direction']): CiGraph =>
  ({ rootCiId: host.id, direction, maxDepth: 3, maxDepthReached: false, containsCycle: false, nodes: [], edges: [] })

/** esx-01 → core-sw-01 → dc1-core-rtr: two hops, so the tree has a route to draw rather than a flat pair. */
const ancestorGraph: CiGraph = {
  ...emptyGraph('Ancestors'),
  nodes: [
    { id: switchCi.id, type: 'NetworkDevice', name: 'core-sw-01', assetTag: 'AT-7001', lifecycleState: 'Deployed', isActive: true, depth: 1 },
    { id: 'ci-rtr', type: 'NetworkDevice', name: 'dc1-core-rtr', assetTag: null, lifecycleState: 'InRepair', isActive: true, depth: 2 },
  ],
  edges: [
    { id: 'edge-1', sourceCiId: host.id, targetCiId: switchCi.id, type: 'ConnectsTo' },
    { id: 'edge-9', sourceCiId: switchCi.id, targetCiId: 'ci-rtr', type: 'DependsOn' },
  ],
}

/** esx-01 connects to core-sw-01 (upstream) and vm-payroll runs on esx-01 (downstream). */
const relationships: CiRelationships = {
  ciId: host.id,
  upstream: [{ id: 'edge-1', sourceCiId: host.id, sourceCiName: 'esx-01', sourceCiType: 'Server', targetCiId: switchCi.id, targetCiName: 'core-sw-01', targetCiType: 'NetworkDevice', type: 'ConnectsTo', description: 'Uplink on port 12', createdBy: 'technician1', createdAt: '2026-08-03T00:00:00Z' }],
  downstream: [{ id: 'edge-2', sourceCiId: vm.id, sourceCiName: 'vm-payroll', sourceCiType: 'Virtual', targetCiId: host.id, targetCiName: 'esx-01', targetCiType: 'Server', type: 'RunsOn', description: null, createdBy: 'technician1', createdAt: '2026-08-04T00:00:00Z' }],
}

const noRelationships: CiRelationships = { ciId: host.id, upstream: [], downstream: [] }

function renderCard(ci: Ci = host) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><CiRelationsGraph ci={ci} /></QueryClientProvider></MemoryRouter>)
}

/** Opens one edge card's ⋮ menu, which is where the per-edge actions now live. */
async function openMenu(user: ReturnType<typeof userEvent.setup>, edge: string) {
  await user.click(await screen.findByRole('button', { name: `Actions for ${edge}` }))
}

/** Walks the picker: open the dialog, search, and select the named candidate. */
async function choose(user: ReturnType<typeof userEvent.setup>, name: string) {
  await user.click(await screen.findByRole('button', { name: /Relate to/ }))
  const dialog = await screen.findByRole('dialog')
  const row = (await within(dialog).findByText(name)).closest('li')!
  await user.click(within(row).getByRole('button', { name: 'Select' }))
  return dialog
}

describe('CiRelationsGraph write surface', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.getAncestors).mockResolvedValue(emptyGraph('Ancestors'))
    vi.mocked(assetsApi.getImpactedBy).mockResolvedValue(emptyGraph('Descendants'))
    vi.mocked(assetsApi.getRelationships).mockResolvedValue(relationships)
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [host, switchCi, vm], total: 3, page: 1, pageSize: 10 })
  })

  it('groups each direct edge under a heading that reads correctly for its direction, and links the far end', async () => {
    renderCard()

    const upstream = (await screen.findByRole('link', { name: 'core-sw-01' })).closest('li')!
    expect(upstream).toHaveTextContent('Uplink on port 12')
    expect(upstream).toHaveTextContent('Network device')
    expect(within(upstream).getByRole('link', { name: 'core-sw-01' })).toHaveAttribute('href', '/assets/ci-switch')

    // The open CI is the subject upstream and the object downstream, so the two headings differ.
    expect(screen.getByText('Connects to')).toBeInTheDocument()
    expect(screen.getByText('Runs on this CI')).toBeInTheDocument()
    expect(screen.getByText('2 direct relationships')).toBeInTheDocument()
  })

  // The header used to count every edge in the 3-hop traversal, which on a hub CI reported a number
  // several times longer than the list printed underneath it.
  it('counts only the CI\'s own edges in the header, not the ones the traversal reached', async () => {
    vi.mocked(assetsApi.getAncestors).mockResolvedValue(ancestorGraph)
    renderCard()

    expect(await screen.findByText('2 direct relationships')).toBeInTheDocument()
  })

  it('draws the dependency path as a route, naming the edge walked to each CI', async () => {
    vi.mocked(assetsApi.getAncestors).mockResolvedValue(ancestorGraph)
    renderCard()

    // dc1-core-rtr is two hops out, so it must sit inside core-sw-01's branch rather than beside it.
    const path = within(await screen.findByRole('list', { name: 'Dependency path' }))
    const router = path.getByRole('link', { name: 'dc1-core-rtr' }).closest('li')!
    const switchBranch = path.getByRole('link', { name: 'core-sw-01' }).closest('li')!
    expect(switchBranch).toContainElement(router)
    expect(router).toHaveTextContent('depends on')
    expect(router).toHaveTextContent('In repair')
    expect(screen.getByText('2 levels shown')).toBeInTheDocument()
    expect(screen.getByText('Current CI')).toBeInTheDocument()
  })

  it('names the empty side rather than leaving it blank', async () => {
    vi.mocked(assetsApi.getAncestors).mockResolvedValue(ancestorGraph)
    renderCard()

    expect(await screen.findByText('No downstream dependencies recorded.')).toBeInTheDocument()
  })

  it('asks the server for more hops when the walk stopped at the ceiling', async () => {
    const user = userEvent.setup()
    vi.mocked(assetsApi.getAncestors).mockResolvedValue({ ...ancestorGraph, maxDepthReached: true })
    renderCard()

    await user.click(await screen.findByRole('button', { name: /Show deeper/ }))

    await waitFor(() => expect(assetsApi.getAncestors).toHaveBeenCalledWith(host.id, 6))
    // The other direction is untouched: each path owns its own depth.
    expect(assetsApi.getImpactedBy).not.toHaveBeenCalledWith(host.id, 6)
  })

  it('offers no way deeper when the walk already reached everything', async () => {
    vi.mocked(assetsApi.getAncestors).mockResolvedValue(ancestorGraph)
    renderCard()

    await screen.findByText('2 levels shown')
    expect(screen.queryByRole('button', { name: /Show deeper/ })).not.toBeInTheDocument()
  })

  it('creates the edge with this CI as the source, the chosen type and a trimmed description', async () => {
    const user = userEvent.setup()
    vi.mocked(assetsApi.createRelationship).mockResolvedValue({ ...relationships.upstream[0], id: 'edge-3', targetCiId: vm.id, targetCiName: 'vm-payroll', targetCiType: 'Virtual', type: 'DependsOn' })
    renderCard()

    const dialog = await choose(user, 'vm-payroll')
    await user.selectOptions(within(dialog).getByLabelText('Relationship type'), 'RunsOn')
    await user.type(within(dialog).getByLabelText(/Description/), '  Payroll cluster  ')
    await user.click(within(dialog).getByRole('button', { name: /Create relationship/ }))

    await waitFor(() => expect(assetsApi.createRelationship).toHaveBeenCalledWith(host.id, {
      targetCiId: vm.id, type: 'RunsOn', description: 'Payroll cluster',
    }))
  })

  // The WP's failure path: the server owns the guard, so the candidate is offered and the 400 that
  // comes back is shown against the CI that was chosen rather than as an unattributable toast.
  it('shows a self-relation 400 as an inline error beside the chosen CI and stays open', async () => {
    const user = userEvent.setup()
    vi.mocked(assetsApi.createRelationship).mockRejectedValue(
      new ApiError(400, 'One or more validation errors occurred.', { TargetCiId: ['A CI cannot be related to itself.'] }))
    renderCard()

    const dialog = await choose(user, 'esx-01')
    await user.click(within(dialog).getByRole('button', { name: /Create relationship/ }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('A CI cannot be related to itself.')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(toasts.success).not.toHaveBeenCalled()
  })

  it('shows a duplicate 409 against the chosen CI', async () => {
    const user = userEvent.setup()
    vi.mocked(assetsApi.createRelationship).mockRejectedValue(new ApiError(409, "'esx-01' already ConnectsTo 'core-sw-01'."))
    renderCard()

    const dialog = await choose(user, 'core-sw-01')
    await user.click(within(dialog).getByRole('button', { name: /Create relationship/ }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("already ConnectsTo 'core-sw-01'")
  })

  // Opening the menu is not the confirmation: the two-step remove survives the move behind ⋮.
  it('removes an edge only after the remove is confirmed', async () => {
    const user = userEvent.setup()
    vi.mocked(assetsApi.deleteRelationship).mockResolvedValue(undefined)
    renderCard()

    await openMenu(user, 'esx-01 connects to core-sw-01')
    await user.click(screen.getByRole('button', { name: /Remove relationship: esx-01 connects to core-sw-01/ }))
    expect(assetsApi.deleteRelationship).not.toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: /Confirm remove/ }))
    await waitFor(() => expect(assetsApi.deleteRelationship).toHaveBeenCalledWith('edge-1'))
  })

  it('surfaces a failed removal on the card', async () => {
    const user = userEvent.setup()
    vi.mocked(assetsApi.deleteRelationship).mockRejectedValue(new ApiError(404, 'Relationship not found.'))
    renderCard()

    await openMenu(user, 'esx-01 connects to core-sw-01')
    await user.click(screen.getByRole('button', { name: /Remove relationship: esx-01 connects to core-sw-01/ }))
    await user.click(screen.getByRole('button', { name: /Confirm remove/ }))

    expect(await screen.findByText('Relationship not found.')).toBeInTheDocument()
  })

  it('opens the related asset from the edge menu', async () => {
    const user = userEvent.setup()
    renderCard()

    await openMenu(user, 'esx-01 connects to core-sw-01')
    expect(screen.getByRole('link', { name: /Open asset/ })).toHaveAttribute('href', '/assets/ci-switch')
  })

  it('refuses to open the editor for a disposed CI and says why', async () => {
    vi.mocked(assetsApi.getRelationships).mockResolvedValue(noRelationships)
    renderCard({ ...host, lifecycleState: 'Disposed' })

    expect(await screen.findByRole('button', { name: /Relate to/ })).toBeDisabled()
    expect(screen.getByText(/closed record/)).toBeInTheDocument()
  })

  it('offers the editor from the empty state when nothing is related yet', async () => {
    vi.mocked(assetsApi.getRelationships).mockResolvedValue(noRelationships)
    renderCard()

    await waitFor(() => expect(screen.getAllByRole('button', { name: /Relate to/ })).toHaveLength(2))
  })
})
