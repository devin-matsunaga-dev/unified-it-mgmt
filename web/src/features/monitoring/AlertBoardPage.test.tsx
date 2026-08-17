import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { assetsApi, type CiImpact } from '../../api/assets'
import type { Alert, AlertPage } from '../../api/monitoring'
import { monitoringApi } from '../../api/monitoring'
import { AlertBoardPage, matches, sortAlerts } from './AlertBoardPage'
import { resetMonitoringHubForTests } from './useMonitoringHub'

vi.mock('../../api/monitoring', async (original) => {
  const actual = await original<typeof import('../../api/monitoring')>()
  return {
    ...actual,
    monitoringApi: {
      ...actual.monitoringApi, listAlerts: vi.fn(), acknowledgeAlert: vi.fn(), getAlert: vi.fn(),
    },
  }
})

// The drawer mounts the WP-5.2 blast-radius panel, which reads the CMDB. Stubbed here rather than left
// to a real request: what these tests are about is the alert, and an unmocked call would put the
// panel's "could not be loaded" alert on every drawer assertion below.
vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, getImpact: vi.fn() } }
})

// The hub opens a real websocket; every screen here reads over HTTP first and only then listens, so
// the boards are testable with it stubbed out.
vi.mock('@microsoft/signalr', async (original) => {
  const actual = await original<typeof import('@microsoft/signalr')>()
  class StubBuilder {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    configureLogging() { return this }
    build() {
      return {
        on: vi.fn(), onreconnecting: vi.fn(), onreconnected: vi.fn(), onclose: vi.fn(),
        start: () => Promise.resolve(), stop: () => Promise.resolve(), state: 'Connected',
      }
    }
  }
  return { ...actual, HubConnectionBuilder: StubBuilder }
})

function alert(overrides: Partial<Alert> = {}): Alert {
  return {
    id: 'alert-1', deviceId: 'device-1', ciId: 'ci-1', checkId: 'check-1',
    ruleId: 'check:check-1:availability', metricName: 'check.success',
    severity: 'Critical', status: 'Open', summary: 'Host is unreachable',
    lastValue: 0, threshold: null, consecutiveBreaches: 3, isFlapping: false, suppression: 'None',
    rootCauseAlertId: null, impactedCount: 0,
    raisedAt: '2026-08-11T11:00:00Z', lastObservedAt: '2026-08-11T11:05:00Z', clearedAt: null,
    pollerName: 'poller-1', acknowledgedAt: null, acknowledgedBy: null, acknowledgedByName: null,
    deviceAddress: '10.40.0.1', checkName: 'Reachability',
    ciFound: true, ciName: 'dc1-core-sw-01', ciType: 'NetworkDevice', assetTag: 'AT-1',
    lifecycleState: 'Deployed', ownerName: 'Tessa Nolan', siteName: 'Primary Data Centre',
    departmentName: 'IT', warrantyExpiresAt: null, warrantyStatus: 'Active',
    warrantyDaysRemaining: 640, contractName: null,
    ...overrides,
  }
}

/** What the CMDB says would follow the alerting switch down (WP-5.2). */
function blastRadius(overrides: Partial<CiImpact> = {}): CiImpact {
  return {
    rootCiId: 'ci-1', rootCiName: 'dc1-core-sw-01', rootCiType: 'NetworkDevice',
    maxDepth: 5, maxDepthReached: false, containsCycle: false,
    summary: {
      ciCount: 3, directCiCount: 2, openTicketCount: 1, breachedSlaCount: 1, atRiskSlaCount: 0,
      nextSlaDueAt: null, affectedUserCount: 1, affectedDepartmentCount: 1, cisWithoutDepartment: 0,
      cisTruncated: false, ticketsTruncated: false,
    },
    cis: [
      { ciId: 'ci-1', name: 'dc1-core-sw-01', type: 'NetworkDevice', lifecycleState: 'Deployed', isActive: true, depth: 0, ownerUserId: null, ownerName: null, departmentId: 'dept-it', departmentName: 'IT', siteName: 'Primary Data Centre', openTicketCount: 0 },
      { ciId: 'ci-esx', name: 'dc1-esx-01', type: 'Server', lifecycleState: 'Deployed', isActive: true, depth: 1, ownerUserId: 'user-1', ownerName: 'Tessa Nolan', departmentId: 'dept-it', departmentName: 'IT', siteName: 'Primary Data Centre', openTicketCount: 1 },
      { ciId: 'ci-db', name: 'dc1-db-01', type: 'Server', lifecycleState: 'Deployed', isActive: true, depth: 1, ownerUserId: 'user-1', ownerName: 'Tessa Nolan', departmentId: 'dept-it', departmentName: 'IT', siteName: 'Primary Data Centre', openTicketCount: 0 },
    ],
    tickets: [{
      ticketId: 'ticket-7', number: 'INC-000077', title: 'Finance ERP is down', status: 'InProgress',
      priority: 'Critical', createdAt: '2026-08-11T09:00:00Z', ciId: 'ci-esx', ciName: 'dc1-esx-01',
      sla: { policyName: 'Standard', resolutionDueAt: '2026-08-11T11:00:00Z', remainingSeconds: 0, breached: true, atRisk: false },
    }],
    departments: [{ departmentId: 'dept-it', name: 'IT', ciCount: 3, openTicketCount: 1 }],
    users: [{ userId: 'user-1', name: 'Tessa Nolan', ciCount: 2, openTicketCount: 1 }],
    ...overrides,
  }
}

function page(items: Alert[]): AlertPage {
  return {
    items, total: items.length, page: 1, pageSize: 200,
    counts: {
      open: items.filter((item) => item.status === 'Open').length,
      critical: items.filter((item) => item.severity === 'Critical').length,
      warning: items.filter((item) => item.severity === 'Warning').length,
      unacknowledged: items.filter((item) => !item.acknowledgedAt).length,
    },
  }
}

function renderBoard(entry = '/monitoring/alerts') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}>
    <MemoryRouter initialEntries={[entry]}><AlertBoardPage /></MemoryRouter>
  </QueryClientProvider>)
}

afterEach(() => {
  resetMonitoringHubForTests()
  vi.clearAllMocks()
})

describe('AlertBoardPage', () => {
  /**
   * The receiving half of a WP-5.5 dashboard deep link: the recent-root-causes card links here, and a
   * severity in the domain's spelling becomes one of this board's own filters.
   */
  it('opens on the critical filter when a deep link names that severity', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))

    renderBoard('/monitoring/alerts?severity=Critical')

    expect(await screen.findByRole('button', { name: 'Critical' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('opens on the default filter when the severity is not one it knows', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))

    renderBoard('/monitoring/alerts?severity=Catastrophic')

    expect(await screen.findByRole('button', { name: 'Open' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('shows an alert with the CMDB context WP-3.7 attaches to it', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))

    renderBoard()

    expect(await screen.findByText('Host is unreachable')).toBeInTheDocument()
    expect(screen.getByText('dc1-core-sw-01')).toBeInTheDocument()
    expect(screen.getByText(/Tessa Nolan/)).toBeInTheDocument()
  })

  /**
   * "Owner: —" on a CI that has been deleted reads as an unowned asset, which is a different fact.
   */
  it('says the CI is missing rather than showing blank owner fields', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([
      alert({ ciFound: false, ciName: null, ownerName: null, siteName: null }),
    ]))

    renderBoard()

    expect(await screen.findByText('Not found in the CMDB')).toBeInTheDocument()
  })

  it('acknowledges an alert and shows who has it', async () => {
    const user = userEvent.setup()
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))
    vi.mocked(monitoringApi.acknowledgeAlert).mockResolvedValue(
      alert({ acknowledgedAt: '2026-08-11T11:10:00Z', acknowledgedByName: 'Ari Whitfield' }))

    renderBoard()
    await user.click(await screen.findByRole('button', { name: 'Acknowledge' }))

    await waitFor(() => expect(monitoringApi.acknowledgeAlert).toHaveBeenCalledWith('alert-1'))
    expect(await screen.findByText('Ari Whitfield')).toBeInTheDocument()
    // The button goes once somebody owns it, so a second operator cannot take it from the board.
    expect(screen.queryByRole('button', { name: 'Acknowledge' })).not.toBeInTheDocument()
  })

  /** A flapping or suppressed rule publishes nothing, so the board is the only place it is visible. */
  it('marks a flapping alert and a suppressed one', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([
      alert({ isFlapping: true, suppression: 'Maintenance' }),
    ]))

    renderBoard()

    expect(await screen.findByText('Flapping')).toBeInTheDocument()
    expect(screen.getByText(/Suppressed: Maintenance/)).toBeInTheDocument()
  })

  /**
   * WP-5.1. The cause carries the size of the outage and the consequence says why it is quiet — an
   * operator scanning the board has to be able to tell "this is the one" from "this is a symptom"
   * without opening either.
   */
  it('marks a root cause with what it is holding down, and a consequence as held', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([
      alert({ id: 'alert-cause', summary: 'Core switch is unreachable', impactedCount: 5 }),
      alert({
        id: 'alert-impacted',
        summary: 'Host is unreachable',
        suppression: 'RootCause',
        rootCauseAlertId: 'alert-cause',
      }),
    ]))

    renderBoard()

    expect(await screen.findByText('Root cause · 5 impacted')).toBeInTheDocument()
    expect(screen.getByText('Suppressed under its root cause')).toBeInTheDocument()
    // The enum member must never reach a screen.
    expect(screen.queryByText(/Suppressed: RootCause/)).not.toBeInTheDocument()
  })

  it('says the estate is quiet rather than showing an empty table', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([]))

    renderBoard()

    expect(await screen.findByText(/the estate is quiet/i)).toBeInTheDocument()
  })
})

describe('board patching', () => {
  /** Worst first, then newest — the same order the server returns, so a push lands where a refetch would. */
  it('sorts critical above warning and newer above older', () => {
    const sorted = sortAlerts([
      alert({ id: 'a', severity: 'Warning', raisedAt: '2026-08-11T12:00:00Z' }),
      alert({ id: 'b', severity: 'Critical', raisedAt: '2026-08-11T10:00:00Z' }),
      alert({ id: 'c', severity: 'Critical', raisedAt: '2026-08-11T11:00:00Z' }),
    ])

    expect(sorted.map((item) => item.id)).toEqual(['c', 'b', 'a'])
  })

  it('drops an alert that no longer belongs under the filter it is being shown in', () => {
    expect(matches(alert({ status: 'Cleared' }), { status: 'Open' })).toBe(false)
    expect(matches(alert({ severity: 'Warning' }), { status: 'Open', severity: 'Critical' })).toBe(false)
    expect(matches(alert({ acknowledgedAt: '2026-08-11T11:10:00Z' }), { acknowledged: false })).toBe(false)
    expect(matches(alert(), { status: 'Open' })).toBe(true)
  })
})

/**
 * Where a WP-3.10 Teams or Slack message lands. The link an operator is paged with carries the alert
 * id, so the board has to open on that alert rather than on a list they then have to search.
 */
describe('AlertBoardPage deep link', () => {
  it('opens the detail drawer for the alert named in the query string', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))
    vi.mocked(monitoringApi.getAlert).mockResolvedValue({
      alert: alert(),
      openTickets: [{
        ticketId: 'ticket-1', number: 'INC-000042', title: 'Switch unreachable',
        status: 'InProgress', priority: 'High', createdAt: '2026-08-11T10:00:00Z',
      }],
      impacted: [],
    })

    renderBoard('/monitoring/alerts?alertId=alert-1')

    const drawer = await screen.findByRole('dialog', { name: 'Alert details' })
    expect(monitoringApi.getAlert).toHaveBeenCalledWith('alert-1')
    // The open tickets are the half of the WP-3.7 context only this endpoint carries.
    expect(within(drawer).getByText('INC-000042')).toBeInTheDocument()
    expect(within(drawer).getByText('Open tickets for this asset (1)')).toBeInTheDocument()
  })

  /**
   * WP-5.2, in the drawer: the alert says one switch is down, and this says what goes with it. The
   * panel reads the CMDB by the alert's own CI id, so an operator triaging a page never has to open
   * the asset to find out how much of the estate is behind it.
   */
  it('shows the blast radius of the alerting asset', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))
    vi.mocked(monitoringApi.getAlert).mockResolvedValue({ alert: alert(), openTickets: [], impacted: [] })
    vi.mocked(assetsApi.getImpact).mockResolvedValue(blastRadius())

    renderBoard('/monitoring/alerts?alertId=alert-1')

    const drawer = await screen.findByRole('dialog', { name: 'Alert details' })
    await waitFor(() => expect(assetsApi.getImpact).toHaveBeenCalledWith('ci-1', 5))
    const radius = within(drawer).getByRole('region', { name: 'Blast radius' })
    expect(await within(radius).findByRole('link', { name: 'dc1-esx-01' })).toHaveAttribute('href', '/assets/ci-esx')
    expect(within(radius).getByText('Finance ERP is down')).toBeInTheDocument()
    expect(within(radius).getByRole('link', { name: /Open the full blast radius/ }))
      .toHaveAttribute('href', '/assets/ci-1')
  })

  /**
   * A blast radius is a walk of the dependency graph, so an alert whose CI has left the CMDB has no
   * node to walk from. Asking anyway would be a 404 rendered as a broken panel.
   */
  it('draws no blast radius for an alert whose CI is not in the CMDB', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert({ ciFound: false, ciName: null })]))
    vi.mocked(monitoringApi.getAlert).mockResolvedValue({
      alert: alert({ ciFound: false, ciName: null }), openTickets: [], impacted: [],
    })

    renderBoard('/monitoring/alerts?alertId=alert-1')

    await screen.findByRole('dialog', { name: 'Alert details' })
    expect(screen.queryByRole('region', { name: 'Blast radius' })).not.toBeInTheDocument()
    expect(assetsApi.getImpact).not.toHaveBeenCalled()
  })

  /**
   * The WP's "5 suppressed alerts visible under it", on screen. They opened no ticket of their own, so
   * the root cause's drawer is where an operator finds out what else went down.
   */
  it('lists the suppressed alerts under a root cause', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert({ impactedCount: 2 })]))
    vi.mocked(monitoringApi.getAlert).mockResolvedValue({
      alert: alert({ impactedCount: 2 }),
      openTickets: [],
      impacted: [
        {
          alertId: 'alert-2', deviceId: 'device-2', ciId: 'ci-2', ciName: 'dc1-esx-01',
          ciType: 'Server', ruleId: 'check:check-2:availability', severity: 'Critical',
          suppression: 'RootCause', summary: 'Host is unreachable',
          raisedAt: '2026-08-11T11:01:00Z',
        },
        {
          alertId: 'alert-3', deviceId: 'device-3', ciId: 'ci-3', ciName: null,
          ciType: null, ruleId: 'check:check-3:availability', severity: 'Warning',
          suppression: 'RootCause', summary: 'Latency is above the warning threshold',
          raisedAt: '2026-08-11T11:02:00Z',
        },
      ],
    })

    renderBoard('/monitoring/alerts?alertId=alert-1')

    const drawer = await screen.findByRole('dialog', { name: 'Alert details' })
    expect(within(drawer).getByText('Suppressed under this alert (2)')).toBeInTheDocument()
    expect(within(drawer).getByText('dc1-esx-01')).toBeInTheDocument()
    // A CI that has left the CMDB is still listed, by id: a shorter list would under-report the outage.
    expect(within(drawer).getByText('CI ci-3')).toBeInTheDocument()
  })

  /** An ordinary alert's drawer is exactly what it was before WP-5.1. */
  it('says nothing about impact on an alert that explains nothing', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))
    vi.mocked(monitoringApi.getAlert).mockResolvedValue({
      alert: alert(), openTickets: [], impacted: [],
    })

    renderBoard('/monitoring/alerts?alertId=alert-1')

    const drawer = await screen.findByRole('dialog', { name: 'Alert details' })
    expect(within(drawer).queryByText(/Suppressed under this alert/)).not.toBeInTheDocument()
  })

  /** A consequence is one click from the cause that silenced it. */
  it('opens the root-cause alert from a suppressed one', async () => {
    const user = userEvent.setup()
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))
    vi.mocked(monitoringApi.getAlert).mockResolvedValue({
      alert: alert({ suppression: 'RootCause', rootCauseAlertId: 'alert-cause' }),
      openTickets: [],
      impacted: [],
    })

    renderBoard('/monitoring/alerts?alertId=alert-1')
    await user.click(await screen.findByRole('button', { name: 'Open the root-cause alert' }))

    await waitFor(() => expect(monitoringApi.getAlert).toHaveBeenCalledWith('alert-cause'))
  })

  it('does not open a drawer when no alert is named', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))

    renderBoard()

    expect(await screen.findByText('Host is unreachable')).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Alert details' })).not.toBeInTheDocument()
    expect(monitoringApi.getAlert).not.toHaveBeenCalled()
  })

  /** A failed read is a fact about the request; "no details" would be a claim about the alert. */
  it('distinguishes an alert that could not be loaded from one with nothing to show', async () => {
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))
    vi.mocked(monitoringApi.getAlert).mockRejectedValue(new Error('Alert not found.'))

    renderBoard('/monitoring/alerts?alertId=alert-1')

    expect(await screen.findByText(/could not be loaded/)).toBeInTheDocument()
  })

  it('opens the drawer when an alert summary is clicked', async () => {
    const user = userEvent.setup()
    vi.mocked(monitoringApi.listAlerts).mockResolvedValue(page([alert()]))
    vi.mocked(monitoringApi.getAlert).mockResolvedValue({ alert: alert(), openTickets: [], impacted: [] })

    renderBoard()
    await user.click(await screen.findByRole('button', { name: 'Host is unreachable' }))

    expect(await screen.findByRole('dialog', { name: 'Alert details' })).toBeInTheDocument()
  })
})
