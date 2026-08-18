import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { monitoringApi, type Alert, type AlertPage, type DeviceStatusTile, type StatusBoard } from '../../api/monitoring'
import { topologyApi, type Topology, type TopologyMap } from '../../api/topology'
import type { MonitoringHubEvents } from '../monitoring/useMonitoringHub'
import { useState, type ReactNode } from 'react'
import { PageHeadingContext, type PageHeading } from '../../layout/pageHeading'
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
  return { ...actual, monitoringApi: { ...actual.monitoringApi, statusBoard: vi.fn(), listAlerts: vi.fn() } }
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
      isActive: true, siteName: 'DC1', address: '10.10.0.1', lastSeenByDiscoveryAt: null, networkRole: null,
    },
    {
      ciId: switchCi, name: 'dc1-core-sw-01', type: 'NetworkDevice', lifecycleState: 'Deployed',
      isActive: true, siteName: 'DC1', address: '10.10.0.2',
      lastSeenByDiscoveryAt: '2026-08-13T12:05:00Z', networkRole: null,
    },
    {
      ciId: strayCi, name: 'dc1-core-sw-02', type: 'NetworkDevice', lifecycleState: 'Deployed',
      isActive: true, siteName: 'DC1', address: '10.10.0.3', lastSeenByDiscoveryAt: null, networkRole: null,
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
  counts: { devices: 1, ok: 1, warning: 0, critical: 0, unknown: 0, disabled: 0 },
}

/**
 * Selects a node by clicking its React Flow wrapper. Deliberately not the name inside it: that is a
 * <Link>, and clicking it navigates rather than selects — and its mousedown reaches d3-drag, which
 * jsdom cannot satisfy. fireEvent rather than userEvent for the same reason.
 */
async function selectNode(name: string) {
  const card = (await screen.findByText(name)).closest('.react-flow__node')
  expect(card).not.toBeNull()
  fireEvent.click(card!)
}

/** A switch with four laptops on it — the shape §2's endpoint folding exists for. */
function estateWithEndpoints(): Topology {
  const laptops = ['lt-1', 'lt-2', 'lt-3', 'lt-4']
  return {
    ...topology,
    nodes: [
      topology.nodes[0], topology.nodes[1],
      ...laptops.map((id) => ({
        ciId: id, name: id, type: 'Hardware' as const, lifecycleState: 'Deployed' as const,
        isActive: true, siteName: 'DC1', address: null, lastSeenByDiscoveryAt: null, networkRole: null,
      })),
    ],
    edges: [
      topology.edges[0],
      ...laptops.map((id) => ({
        id: `${id}->switch`, sourceCiId: id, targetCiId: switchCi, type: 'ConnectsTo' as const,
        description: null, observedByDiscovery: false,
      })),
    ],
    observedLinks: [],
  }
}

const emptyAlerts: AlertPage = {
  items: [], total: 0, page: 1, pageSize: 200,
  counts: { open: 0, critical: 0, warning: 0, unacknowledged: 0 },
}

function openAlert(overrides: Partial<Alert>): Alert {
  return {
    id: 'alert-1', deviceId: switchDevice, ciId: switchCi, checkId: 'check-1', ruleId: 'rule-1',
    metricName: 'icmp.reachable', severity: 'Critical', status: 'Open', summary: 'Unreachable',
    lastValue: 0, threshold: 1, consecutiveBreaches: 3, isFlapping: false,
    suppression: 'None', rootCauseAlertId: null, impactedCount: 0,
    raisedAt: '2026-08-18T00:00:00Z', lastObservedAt: '2026-08-18T00:05:00Z', clearedAt: null,
    pollerName: 'poller-1', acknowledgedAt: null, acknowledgedBy: null, acknowledgedByName: null,
    deviceAddress: '10.10.0.2', checkName: 'Ping', ciFound: true, ciName: 'dc1-core-sw-01',
    ciType: 'NetworkDevice',
    ...overrides,
  } as Alert
}

/**
 * The page's title and its "what am I looking at" line are published to the shell's topbar rather
 * than drawn here, so the harness stands in for the shell and renders what the page hands it.
 */
function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ShellHeading><TopologyPage /></ShellHeading>
      </MemoryRouter>
    </QueryClientProvider>)
}

function ShellHeading({ children }: { children: ReactNode }) {
  const [heading, setHeading] = useState<PageHeading | null>(null)
  return <PageHeadingContext.Provider value={setHeading}>
    <p>{heading?.subtitle}</p>
    {children}
  </PageHeadingContext.Provider>
}

beforeEach(() => {
  hubEvents = {}
  vi.mocked(topologyApi.get).mockResolvedValue(topology)
  vi.mocked(topologyApi.listMaps).mockResolvedValue([])
  vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board)
  vi.mocked(monitoringApi.listAlerts).mockResolvedValue(emptyAlerts)
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

  /**
   * §1: each view is a server-side type cut, so the node limit is spent on what the view is about
   * rather than on things the browser would then throw away.
   */
  it('asks the API for the types the chosen view selects', async () => {
    const user = userEvent.setup()
    renderPage()

    // Overview is the default and deliberately omits Hardware — no wall of laptops on open.
    await waitFor(() => expect(topologyApi.get)
      .toHaveBeenCalledWith(['NetworkDevice', 'Server', 'Virtual', 'Software', 'Logical']))

    await user.click(screen.getByRole('button', { name: 'Network' }))
    await waitFor(() => expect(topologyApi.get).toHaveBeenLastCalledWith(['NetworkDevice']))

    await user.click(screen.getByRole('button', { name: 'Applications' }))
    await waitFor(() => expect(topologyApi.get)
      .toHaveBeenLastCalledWith(['Software', 'Logical', 'Server', 'Virtual']))

    // Everything is the only view that asks for no filter at all.
    await user.click(screen.getByRole('button', { name: 'Everything' }))
    await waitFor(() => expect(topologyApi.get).toHaveBeenLastCalledWith(undefined))
  })

  it('says what the chosen view is showing', async () => {
    const user = userEvent.setup()
    renderPage()

    expect(await screen.findByText(/without the desk equipment/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Network' }))

    expect(screen.getByText(/edge-first by the role each one records/)).toBeInTheDocument()
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
    const sent = vi.mocked(topologyApi.createMap).mock.calls.at(-1)![0]
    expect(sent.name).toBe('Estate topology')
    expect(sent.nodes.map((node) => node.ciId).sort())
      .toEqual([routerCi, strayCi, switchCi].sort())
    expect(sent.nodes.every((node) => Number.isFinite(node.x) && Number.isFinite(node.y))).toBe(true)
  })

  /**
   * §11: selecting a CI has to turn the map into a troubleshooting view — the CI named, its health
   * stated from real monitoring data, and the actions that lead somewhere useful.
   */
  it('names the selected CI and offers the actions for it', async () => {
    renderPage()

    await selectNode('dc1-core-sw-01')

    const bar = screen.getByRole('region', { name: 'Selected CI' })
    expect(within(bar).getByText('dc1-core-sw-01')).toBeInTheDocument()
    expect(within(bar).getByText(/10\.10\.0\.2/)).toBeInTheDocument()
    expect(within(bar).getByRole('button', { name: 'Focus' })).toBeInTheDocument()
    expect(within(bar).getByRole('link', { name: 'Open CI' })).toHaveAttribute('href', `/assets/${switchCi}`)
    // The switch is the monitored one in the fixture, so it also offers its device page.
    expect(within(bar).getByRole('link', { name: 'Device' })).toHaveAttribute('href', `/monitoring/devices/${switchDevice}`)
  })

  /** It counts what it is joined to, so "is this thing load-bearing" is answered before any zooming. */
  it('says how many relationships the selected CI has', async () => {
    renderPage()

    await selectNode('dc1-core-sw-01')

    // Two, not one: the recorded uplink to the router plus the observed link to the second core
    // switch. Both are relationships the map draws, so both count.
    expect(within(screen.getByRole('region', { name: 'Selected CI' })).getByText(/2 direct relationships/))
      .toBeInTheDocument()
  })

  /** §5: unrelated topology fades but stays on the map. Only Focus Mode removes it. */
  it('keeps unrelated CIs on the map when one is merely selected', async () => {
    renderPage()

    await selectNode('dc1-core-sw-01')

    expect(screen.getByText('dc1-core-sw-02')).toBeInTheDocument()
    expect(screen.getByText('dc1-core-rtr-01')).toBeInTheDocument()
  })

  /**
   * §6: Focus Mode isolates the neighbourhood. Everything in the shared fixture is within two hops
   * of the switch, so this adds a CI that genuinely is not — otherwise the test would pass without
   * Focus Mode doing anything at all.
   */
  it('hides everything outside the neighbourhood in focus mode, and brings it back on exit', async () => {
    vi.mocked(topologyApi.get).mockResolvedValue({
      ...topology,
      nodes: [...topology.nodes, {
        ciId: 'ci-far', name: 'br1-sw-01', type: 'NetworkDevice', lifecycleState: 'Deployed',
        isActive: true, siteName: 'BR1', address: '10.30.0.2', lastSeenByDiscoveryAt: null, networkRole: null,
      }],
    })
    const user = userEvent.setup()
    renderPage()

    await selectNode('dc1-core-sw-01')
    await user.click(screen.getByRole('button', { name: 'Focus' }))

    await waitFor(() => expect(screen.queryByText('br1-sw-01')).not.toBeInTheDocument())
    // One hop away, so it stays.
    expect(screen.getByText('dc1-core-rtr-01')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /Focused/ }))

    expect(await screen.findByText('br1-sw-01')).toBeInTheDocument()
  })

  /** There must always be an obvious way out; clearing the selection leaves focus mode with it. */
  it('clearing the selection also leaves focus mode', async () => {
    vi.mocked(topologyApi.get).mockResolvedValue({
      ...topology,
      nodes: [...topology.nodes, {
        ciId: 'ci-far', name: 'br1-sw-01', type: 'NetworkDevice', lifecycleState: 'Deployed',
        isActive: true, siteName: 'BR1', address: '10.30.0.2', lastSeenByDiscoveryAt: null, networkRole: null,
      }],
    })
    const user = userEvent.setup()
    renderPage()

    await selectNode('dc1-core-sw-01')
    await user.click(screen.getByRole('button', { name: 'Focus' }))
    await waitFor(() => expect(screen.queryByText('br1-sw-01')).not.toBeInTheDocument())

    await user.click(screen.getByRole('button', { name: 'Clear selection' }))

    expect(screen.queryByRole('region', { name: 'Selected CI' })).not.toBeInTheDocument()
    expect(await screen.findByText('br1-sw-01')).toBeInTheDocument()
  })

  /** §2: a switch full of laptops draws as one node until somebody asks for the detail. */
  it('folds endpoints into one node per switch and expands them on click', async () => {
    vi.mocked(topologyApi.get).mockResolvedValue(estateWithEndpoints())
    renderPage()

    // Wait for the canvas itself before asserting about its contents: React Flow mounts its nodes
    // after it has measured, and the group is only meaningful once the switch it hangs off is drawn.
    await screen.findByText('dc1-core-sw-01')

    expect(screen.getByText('4 endpoints')).toBeInTheDocument()
    expect(screen.queryByText('lt-1')).not.toBeInTheDocument()

    fireEvent.click(screen.getByText('4 endpoints').closest('.react-flow__node')!)

    expect(await screen.findByText('lt-1')).toBeInTheDocument()
    expect(screen.queryByText('4 endpoints')).not.toBeInTheDocument()

    // And back again. Clicking a member selects it rather than re-folding — the members are real
    // CIs — so the way back is on the switch that owns them.
    const user = userEvent.setup()
    await selectNode('dc1-core-sw-01')
    await user.click(screen.getByRole('button', { name: 'Hide 4 endpoints' }))

    expect(await screen.findByText('4 endpoints')).toBeInTheDocument()
    expect(screen.queryByText('lt-1')).not.toBeInTheDocument()
  })

  /** The switch that owns a folded group says so, and can open it without hunting for the group node. */
  it('offers to show the endpoints from the switch that owns them', async () => {
    vi.mocked(topologyApi.get).mockResolvedValue(estateWithEndpoints())
    const user = userEvent.setup()
    renderPage()

    await screen.findByText('dc1-core-sw-01')
    await selectNode('dc1-core-sw-01')

    await user.click(screen.getByRole('button', { name: 'Show 4 endpoints' }))

    expect(await screen.findByText('lt-1')).toBeInTheDocument()
  })

  /**
   * The saved-map payload is built from whatever is on the canvas. A group is a drawing, so its
   * synthetic id must never be posted as a pinned CI — the server would be handed an id no CI has.
   */
  it('never sends an endpoint group as a pinned CI when a layout is saved', async () => {
    vi.mocked(topologyApi.get).mockResolvedValue(estateWithEndpoints())
    vi.mocked(topologyApi.createMap).mockResolvedValue({
      id: 'map-1', name: 'Estate topology', description: null, nodes: [],
      createdBy: 'admin1', createdAt: '2026-08-18T00:00:00Z', updatedBy: null, updatedAt: '2026-08-18T00:00:00Z',
    })
    vi.spyOn(window, 'prompt').mockReturnValue('Estate topology')
    const user = userEvent.setup()
    renderPage()

    await screen.findByText('4 endpoints')
    // Reset marks the layout dirty, which is what enables saving.
    await user.click(screen.getByRole('button', { name: /Reset to auto-layout/ }))
    await user.click(screen.getByRole('button', { name: /Save as new map/ }))

    await waitFor(() => expect(topologyApi.createMap).toHaveBeenCalled())
    const sent = vi.mocked(topologyApi.createMap).mock.calls.at(-1)![0]
    expect(sent.nodes.every((pin) => !pin.ciId.startsWith('endpoint-group:'))).toBe(true)
    expect(sent.nodes.map((pin) => pin.ciId).sort()).toEqual(['ci-router', 'ci-switch'])
  })

  /**
   * §13: the cause and the CIs it took out have to be told apart, and the distinction comes from the
   * correlation engine (WP-5.1) rather than from anything this page works out.
   */
  it('marks the root cause and the CIs suppressed under it', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue({
      ...emptyAlerts,
      items: [
        openAlert({ id: 'cause', ciId: switchCi, impactedCount: 2 }),
        openAlert({ id: 'downstream', ciId: routerCi, suppression: 'RootCause', rootCauseAlertId: 'cause' }),
      ],
      total: 2,
    })
    renderPage()

    const causeCard = (await screen.findByText('dc1-core-sw-01')).closest('.react-flow__node')!
    expect(within(causeCard as HTMLElement).getByText('Root cause')).toBeInTheDocument()

    const affectedCard = screen.getByText('dc1-core-rtr-01').closest('.react-flow__node')!
    expect(within(affectedCard as HTMLElement).getByText('Affected')).toBeInTheDocument()
  })

  /** §4: an operator scanning the map should see how much is wrong without opening anything. */
  it('shows the open alert count on a node', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue({
      ...emptyAlerts,
      items: [openAlert({ id: 'a' }), openAlert({ id: 'b' })],
      total: 2,
    })
    renderPage()

    const card = (await screen.findByText('dc1-core-sw-01')).closest('.react-flow__node')!
    expect(within(card as HTMLElement).getByText(/2 alerts/)).toBeInTheDocument()
  })

  /** A quiet estate says nothing about alerts rather than showing a zero on every node. */
  it('says nothing about alerts when there are none', async () => {
    renderPage()

    const card = (await screen.findByText('dc1-core-sw-01')).closest('.react-flow__node')!
    expect(within(card as HTMLElement).queryByText(/alert/)).not.toBeInTheDocument()
    expect(within(card as HTMLElement).queryByText('Root cause')).not.toBeInTheDocument()
  })

  /** §7: the cut is built from the relationship model that exists, and only offers live options. */
  it('offers the relationship cuts the graph actually has', async () => {
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('dc1-core-sw-01')

    const control = screen.getByLabelText('Relationships')
    // Only the cuts with something behind them are offered: the fixture has one ConnectsTo edge and
    // one discovery-only observation, and no DependsOn at all.
    expect([...control.querySelectorAll('option')].map((option) => option.textContent))
      .toEqual(['All relationships', 'Recorded only', 'Discovered only', 'Connects to'])

    // The recorded uplink goes; the observation nobody wrote down stays.
    await user.selectOptions(control, 'discovered')

    expect(control).toHaveValue('discovered')
    // Which edges survive the cut is asserted in relationshipFilters.test.ts, not here: React Flow
    // computes edge paths from measured node dimensions, and jsdom measures nothing, so no edge
    // element is ever rendered in this environment.
  })

  /** §8: sites are drawn as subtle boundaries, and can be turned off. */
  it('labels each site and lets the boundaries be hidden', async () => {
    const user = userEvent.setup()
    renderPage()

    expect(await screen.findByText('DC1')).toBeInTheDocument()

    await user.click(screen.getByLabelText('Sites'))

    expect(screen.queryByText('DC1')).not.toBeInTheDocument()
  })
})
