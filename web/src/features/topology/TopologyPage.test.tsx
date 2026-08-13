import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { monitoringApi, type DeviceStatusTile, type StatusBoard } from '../../api/monitoring'
import { topologyApi, type Topology, type TopologyMap } from '../../api/topology'
import type { MonitoringHubEvents } from '../monitoring/useMonitoringHub'
import { TopologyPage } from './TopologyPage'

vi.mock('../../api/topology', async (original) => {
  const actual = await original<typeof import('../../api/topology')>()
  return {
    ...actual,
    topologyApi: {
      get: vi.fn(),
      listMaps: vi.fn(),
      getMap: vi.fn(),
      createMap: vi.fn(),
      updateMap: vi.fn(),
      deleteMap: vi.fn(),
    },
  }
})

vi.mock('../../api/monitoring', async (original) => {
  const actual = await original<typeof import('../../api/monitoring')>()
  return { ...actual, monitoringApi: { ...actual.monitoringApi, statusBoard: vi.fn() } }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

/** The hub is captured rather than opened, so a test can push a status change the way the server would. */
let hubEvents: MonitoringHubEvents = {}
vi.mock('../monitoring/useMonitoringHub', () => ({
  useMonitoringHub: (events: MonitoringHubEvents) => {
    hubEvents = events
    return 'live'
  },
}))

const switchCi = 'ci-switch'
const routerCi = 'ci-router'
const strayCi = 'ci-stray'
const switchDevice = 'device-switch'

const topology: Topology = {
  nodes: [
    {
      ciId: routerCi, name: 'dc1-core-rtr-01', type: 'NetworkDevice', lifecycleState: 'Deployed',
      isActive: true, siteName: 'DC1', address: '10.10.0.1', lastSeenByDiscoveryAt: null,
    },
    {
      ciId: switchCi, name: 'dc1-core-sw-01', type: 'NetworkDevice', lifecycleState: 'Deployed',
      isActive: true, siteName: 'DC1', address: '10.10.0.2',
      lastSeenByDiscoveryAt: '2026-08-13T12:05:00Z',
    },
    {
      ciId: strayCi, name: 'dc1-core-sw-02', type: 'NetworkDevice', lifecycleState: 'Deployed',
      isActive: true, siteName: 'DC1', address: '10.10.0.3', lastSeenByDiscoveryAt: null,
    },
  ],
  edges: [
    {
      id: 'edge-1', sourceCiId: switchCi, targetCiId: routerCi, type: 'ConnectsTo',
      description: 'Core uplink to the edge router.', observedByDiscovery: true,
    },
  ],
  observedLinks: [
    {
      id: `observed:${switchCi}:${routerCi}`, sourceCiId: switchCi, targetCiId: routerCi,
      protocols: ['lldp'], sourcePort: 'GigabitEthernet0/1', targetPort: 'GigabitEthernet0/24',
      confirmedByBothEnds: false, matchesAssertedEdge: true,
    },
    {
      id: `observed:${switchCi}:${strayCi}`, sourceCiId: switchCi, targetCiId: strayCi,
      protocols: ['lldp'], sourcePort: 'GigabitEthernet0/2', targetPort: 'GigabitEthernet0/2',
      confirmedByBothEnds: false, matchesAssertedEdge: false,
    },
  ],
  unresolvedNeighbours: [
    {
      reportedByCiId: switchCi, reportedByCiName: 'dc1-core-sw-01', protocol: 'lldp',
      localPort: 'GigabitEthernet0/9', remoteSystemName: 'lab-printer-01', remotePort: 'eth0',
      remoteAddress: null, reason: 'NoCandidate',
    },
  ],
  nodeLimit: 400,
  nodeLimitReached: false,
}

function tile(overrides: Partial<DeviceStatusTile> = {}): DeviceStatusTile {
  return {
    deviceId: switchDevice, ciId: switchCi, ciName: 'dc1-core-sw-01', ciType: 'NetworkDevice',
    siteName: 'DC1', address: '10.10.0.2', pollerGroup: 'default', isEnabled: true,
    status: 'Ok', severity: 'Ok', openAlerts: 0, criticalAlerts: 0, warningAlerts: 0,
    acknowledgedAlerts: 0, checkCount: 3, headline: null, worstAlertRaisedAt: null,
    lastTelemetryAt: '2026-08-13T12:05:00Z',
    ...overrides,
  }
}

const board: StatusBoard = {
  items: [tile()],
  total: 1,
  page: 1,
  pageSize: 200,
  counts: { devices: 1, ok: 1, warning: 0, critical: 0, unknown: 0, disabled: 0, openAlerts: 0 },
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><TopologyPage /></MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  hubEvents = {}
  vi.mocked(topologyApi.get).mockResolvedValue(topology)
  vi.mocked(topologyApi.listMaps).mockResolvedValue([])
  vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board)
})

describe('TopologyPage', () => {
  it('draws a node per CI, with the address under the name', async () => {
    renderPage()

    expect(await screen.findByText('dc1-core-sw-01')).toBeInTheDocument()
    expect(screen.getByText('dc1-core-rtr-01')).toBeInTheDocument()
    expect(screen.getByText('dc1-core-sw-02')).toBeInTheDocument()
    expect(screen.getByText(/10\.10\.0\.2/)).toBeInTheDocument()
  })

  /**
   * The WP's second verification step. A push recolours the node it names and touches nothing else —
   * this is the whole of "stop a device → node goes red live".
   */
  it('recolours a node when the hub reports the device critical', async () => {
    renderPage()
    await screen.findByText('dc1-core-sw-01')
    await waitFor(() => expect(screen.getByText(/Healthy/)).toBeInTheDocument())

    hubEvents.onDeviceStatusChanged?.(tile({ status: 'Critical', severity: 'Critical', criticalAlerts: 1, openAlerts: 1 }))

    expect(await screen.findByText(/Critical/)).toBeInTheDocument()
    // The router is monitored by nothing, so it stays neutral rather than turning green or red.
    expect(screen.getAllByText(/Not monitored/)).toHaveLength(2)
  })

  /**
   * A CI nothing monitors has no status, and a map that painted it green would be asserting health
   * nobody ever measured.
   */
  it('says a CI is not monitored rather than calling it healthy', async () => {
    renderPage()
    await screen.findByText('dc1-core-rtr-01')

    await waitFor(() => expect(screen.getAllByText(/Not monitored/).length).toBeGreaterThan(0))
  })

  /** Click-through goes to the device page when there is a device, and to the CI page when there is not. */
  it('links a monitored node to its device page and an unmonitored one to its CI', async () => {
    renderPage()

    // Queried through the text rather than the link role: React Flow keeps a node `visibility:
    // hidden` until its ResizeObserver reports a size, and jsdom has no layout to report — so the
    // anchors are in the document but out of the accessibility tree for the whole test.
    const monitored = (await screen.findByText('dc1-core-sw-01')).closest('a')
    await waitFor(() => expect(monitored).toHaveAttribute('href', `/monitoring/devices/${switchDevice}`))
    expect(screen.getByText('dc1-core-rtr-01').closest('a')).toHaveAttribute('href', `/assets/${routerCi}`)
  })

  it('lists a neighbour no CI answers to instead of drawing it', async () => {
    renderPage()

    const section = await screen.findByRole('heading', { name: /Neighbours with no CI \(1\)/ })
    const list = section.closest('section')!
    expect(within(list).getByText('lab-printer-01')).toBeInTheDocument()
    expect(within(list).getByText(/No CI is named that or records that address/)).toBeInTheDocument()
    // Not a node: it has no CI page to open, so it gets no card.
    expect(within(list).getByText('lab-printer-01').closest('a')).toBeNull()
  })

  it('asks the API only for the types the filter selects', async () => {
    renderPage()
    await screen.findByText('dc1-core-sw-01')

    await userEvent.click(screen.getByRole('button', { name: 'Network and servers' }))

    await waitFor(() => expect(vi.mocked(topologyApi.get)).toHaveBeenCalledWith(['NetworkDevice', 'Server', 'Virtual']))
  })

  it('says so when the estate is bigger than the map will draw', async () => {
    vi.mocked(topologyApi.get).mockResolvedValue({ ...topology, nodeLimitReached: true, nodeLimit: 400 })
    renderPage()

    expect(await screen.findByText(
      'Showing the 400 most connected CIs. This is part of the estate, not all of it.'))
      .toBeInTheDocument()
  })

  it('applies a saved layout over the automatic one', async () => {
    const saved: TopologyMap = {
      id: 'map-1', name: 'NOC wall', description: null,
      nodes: [{ ciId: switchCi, x: 640, y: 480 }],
      createdBy: 'technician1', createdAt: '2026-08-13T10:00:00Z', updatedBy: null,
      updatedAt: '2026-08-13T10:00:00Z',
    }
    vi.mocked(topologyApi.listMaps).mockResolvedValue([{ ...saved, pinnedNodeCount: 1 }])
    vi.mocked(topologyApi.getMap).mockResolvedValue(saved)
    renderPage()
    await screen.findByText('dc1-core-sw-01')

    await userEvent.selectOptions(screen.getByLabelText('Saved map'), 'map-1')

    await waitFor(() => expect(vi.mocked(topologyApi.getMap)).toHaveBeenCalledWith('map-1'))
    // The pinned node reports itself as pinned; the others fall back to auto-layout.
    expect(await screen.findByLabelText('Pinned')).toBeInTheDocument()
  })

  /** Nothing is saved until an operator asks: the button is dead until the canvas has moved. */
  it('will not save a layout nobody has changed', async () => {
    renderPage()
    await screen.findByText('dc1-core-sw-01')

    expect(screen.getByRole('button', { name: /Save as new map/ })).toBeDisabled()
  })

  it('offers to reset to the automatic layout', async () => {
    renderPage()
    await screen.findByText('dc1-core-sw-01')

    await userEvent.click(screen.getByRole('button', { name: /Reset to auto-layout/ }))

    expect(screen.getByRole('button', { name: /Save as new map/ })).toBeEnabled()
  })

  /** Failure path: a rejected save says why and leaves the canvas exactly as the operator left it. */
  it('surfaces a refused save without discarding the arrangement', async () => {
    const { toast } = await import('sonner')
    vi.mocked(topologyApi.createMap).mockRejectedValue(new ApiError(409, 'That map name is taken.'))
    vi.spyOn(window, 'prompt').mockReturnValue('Estate topology')
    renderPage()
    await screen.findByText('dc1-core-sw-01')
    await userEvent.click(screen.getByRole('button', { name: /Reset to auto-layout/ }))

    await userEvent.click(screen.getByRole('button', { name: /Save as new map/ }))

    await waitFor(() => expect(vi.mocked(toast.error)).toHaveBeenCalledWith('That map name is taken.'))
    // Still dirty, so the operator can fix the name and try again rather than re-arranging.
    expect(screen.getByRole('button', { name: /Save as new map/ })).toBeEnabled()
    expect(screen.getByText('dc1-core-sw-01')).toBeInTheDocument()
  })

  it('sends every position on the canvas when a layout is saved', async () => {
    vi.mocked(topologyApi.createMap).mockImplementation(async (input) => ({
      id: 'map-new', name: input.name, description: null, nodes: input.nodes,
      createdBy: 'technician1', createdAt: '2026-08-13T10:00:00Z', updatedBy: null,
      updatedAt: '2026-08-13T10:00:00Z',
    }))
    vi.spyOn(window, 'prompt').mockReturnValue('Estate topology')
    renderPage()
    await screen.findByText('dc1-core-sw-01')
    await userEvent.click(screen.getByRole('button', { name: /Reset to auto-layout/ }))

    await userEvent.click(screen.getByRole('button', { name: /Save as new map/ }))

    await waitFor(() => expect(vi.mocked(topologyApi.createMap)).toHaveBeenCalled())
    const sent = vi.mocked(topologyApi.createMap).mock.calls[0][0]
    expect(sent.name).toBe('Estate topology')
    expect(sent.nodes.map((node) => node.ciId).sort())
      .toEqual([routerCi, strayCi, switchCi].sort())
    expect(sent.nodes.every((node) => Number.isFinite(node.x) && Number.isFinite(node.y))).toBe(true)
  })
})
