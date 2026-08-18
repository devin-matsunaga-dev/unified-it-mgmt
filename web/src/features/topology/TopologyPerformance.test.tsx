import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { monitoringApi, type AlertPage, type StatusBoard } from '../../api/monitoring'
import { topologyApi, type Topology } from '../../api/topology'
import * as layout from './layout'
import { TopologyPage } from './TopologyPage'

vi.mock('../../api/topology', async (original) => {
  const actual = await original<typeof import('../../api/topology')>()
  return {
    ...actual,
    topologyApi: { ...actual.topologyApi, get: vi.fn(), listMaps: vi.fn(), getMap: vi.fn() },
  }
})

vi.mock('../../api/monitoring', async (original) => {
  const actual = await original<typeof import('../../api/monitoring')>()
  return {
    ...actual,
    monitoringApi: { ...actual.monitoringApi, statusBoard: vi.fn(), listAlerts: vi.fn() },
  }
})

vi.mock('../monitoring/useMonitoringHub', () => ({ useMonitoringHub: () => 'live' }))
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const topology: Topology = {
  nodes: [
    {
      ciId: 'ci-router', name: 'edge-router', type: 'NetworkDevice', lifecycleState: 'Deployed',
      isActive: true, siteName: 'HQ', address: '10.0.0.1', lastSeenByDiscoveryAt: null, networkRole: 'Edge',
    },
    {
      ciId: 'ci-switch', name: 'core-switch', type: 'NetworkDevice', lifecycleState: 'Deployed',
      isActive: true, siteName: 'HQ', address: '10.0.0.2', lastSeenByDiscoveryAt: null, networkRole: 'Core',
    },
  ],
  edges: [{
    id: 'edge-1', sourceCiId: 'ci-switch', targetCiId: 'ci-router', type: 'ConnectsTo',
    description: null, observedByDiscovery: false,
  }],
  observedLinks: [{
    id: 'observed:ci-switch:ci-router', sourceCiId: 'ci-switch', targetCiId: 'ci-router',
    protocols: ['lldp'], sourcePort: null, targetPort: null,
    confirmedByBothEnds: false, matchesAssertedEdge: false,
  }],
  unresolvedNeighbours: [],
  nodeLimit: 400,
  nodeLimitReached: false,
}

const board: StatusBoard = {
  items: [], total: 0, page: 1, pageSize: 200,
  counts: { devices: 0, ok: 0, warning: 0, critical: 0, unknown: 0, disabled: 0 },
}

const alerts: AlertPage = {
  items: [], total: 0, page: 1, pageSize: 200,
  counts: { open: 0, critical: 0, warning: 0, unacknowledged: 0 },
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><TopologyPage /></MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.restoreAllMocks()
  vi.mocked(topologyApi.get).mockResolvedValue(topology)
  vi.mocked(topologyApi.listMaps).mockResolvedValue([])
  vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board)
  vi.mocked(monitoringApi.listAlerts).mockResolvedValue(alerts)
})

describe('topology recomputation', () => {
  /**
   * §15: the layout is the expensive part, and it must only run when the topology structure changes.
   * Narrowing which relationships are drawn is a drawing change — re-running the layout for it would
   * also throw away every position an operator had dragged.
   */
  it('does not re-run the layout when the relationship filter changes', async () => {
    const resolveLayout = vi.spyOn(layout, 'resolveLayout')
    const user = userEvent.setup()
    renderPage()

    await screen.findByText('core-switch')
    const afterLoad = resolveLayout.mock.calls.length
    expect(afterLoad).toBeGreaterThan(0)

    await user.selectOptions(screen.getByLabelText('Relationships'), 'discovered')
    await waitFor(() => expect(screen.getByLabelText('Relationships')).toHaveValue('discovered'))

    expect(resolveLayout.mock.calls.length).toBe(afterLoad)
  })

  /** Selecting a CI restyles what is already drawn; it is not a reason to lay the graph out again. */
  it('does not re-run the layout when a CI is selected or focused', async () => {
    const resolveLayout = vi.spyOn(layout, 'resolveLayout')
    const user = userEvent.setup()
    renderPage()

    const card = (await screen.findByText('core-switch')).closest('.react-flow__node')!
    const afterLoad = resolveLayout.mock.calls.length

    // fireEvent, not userEvent: a full pointer sequence on a React Flow node reaches d3-drag, which
    // reads event.view.document — null under jsdom.
    fireEvent.click(card)
    await screen.findByRole('region', { name: 'Selected CI' })
    await user.click(screen.getByRole('button', { name: 'Focus' }))

    expect(resolveLayout.mock.calls.length).toBe(afterLoad)
  })

  /** Turning the site boundaries or the minimap off is chrome, not topology. */
  it('does not re-run the layout when the chrome is toggled', async () => {
    const resolveLayout = vi.spyOn(layout, 'resolveLayout')
    const user = userEvent.setup()
    renderPage()

    await screen.findByText('core-switch')
    const afterLoad = resolveLayout.mock.calls.length

    await user.click(screen.getByLabelText('Sites'))
    await user.click(screen.getByLabelText('Minimap'))

    expect(resolveLayout.mock.calls.length).toBe(afterLoad)
  })
})
