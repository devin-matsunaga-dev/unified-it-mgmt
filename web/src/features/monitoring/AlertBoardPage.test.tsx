import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Alert, AlertPage } from '../../api/monitoring'
import { monitoringApi } from '../../api/monitoring'
import { AlertBoardPage, matches, sortAlerts } from './AlertBoardPage'
import { resetMonitoringHubForTests } from './useMonitoringHub'

vi.mock('../../api/monitoring', async (original) => {
  const actual = await original<typeof import('../../api/monitoring')>()
  return {
    ...actual,
    monitoringApi: { ...actual.monitoringApi, listAlerts: vi.fn(), acknowledgeAlert: vi.fn() },
  }
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

function renderBoard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}>
    <MemoryRouter><AlertBoardPage /></MemoryRouter>
  </QueryClientProvider>)
}

afterEach(() => {
  resetMonitoringHubForTests()
  vi.clearAllMocks()
})

describe('AlertBoardPage', () => {
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
