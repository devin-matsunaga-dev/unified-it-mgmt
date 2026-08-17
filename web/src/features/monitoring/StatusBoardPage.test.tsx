import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { DeviceStatusTile, StatusBoard } from '../../api/monitoring'
import { monitoringApi } from '../../api/monitoring'
import { StatusBoardPage } from './StatusBoardPage'
import { resetMonitoringHubForTests } from './useMonitoringHub'

vi.mock('../../api/monitoring', async (original) => {
  const actual = await original<typeof import('../../api/monitoring')>()
  return { ...actual, monitoringApi: { ...actual.monitoringApi, statusBoard: vi.fn() } }
})

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

function tile(overrides: Partial<DeviceStatusTile> = {}): DeviceStatusTile {
  return {
    deviceId: 'device-1', ciId: 'ci-1', ciName: 'dc1-core-sw-01', ciType: 'NetworkDevice',
    siteName: 'Primary Data Centre', address: '10.40.0.1', pollerGroup: 'default', isEnabled: true,
    status: 'Ok', severity: 'Ok', openAlerts: 0, criticalAlerts: 0, warningAlerts: 0,
    acknowledgedAlerts: 0, checkCount: 4, headline: null, worstAlertRaisedAt: null,
    lastTelemetryAt: '2026-08-11T11:59:00Z',
    ...overrides,
  }
}

function board(items: DeviceStatusTile[]): StatusBoard {
  return {
    items, total: items.length, page: 1, pageSize: 200,
    counts: {
      devices: items.length,
      ok: items.filter((item) => item.status === 'Ok').length,
      warning: items.filter((item) => item.status === 'Warning').length,
      critical: items.filter((item) => item.status === 'Critical').length,
      unknown: items.filter((item) => item.status === 'Unknown').length,
      disabled: items.filter((item) => item.status === 'Disabled').length,
    },
  }
}

function renderBoard(entry = '/monitoring') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}>
    <MemoryRouter initialEntries={[entry]}><StatusBoardPage /></MemoryRouter>
  </QueryClientProvider>)
}

afterEach(() => {
  resetMonitoringHubForTests()
  vi.clearAllMocks()
})

describe('StatusBoardPage', () => {
  /**
   * The receiving half of a WP-5.5 dashboard deep link: the Critical band on the network-status widget
   * opens this board already narrowed to the devices it counted.
   */
  it('opens filtered to the status a deep link names', async () => {
    vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board([
      tile({ deviceId: 'device-1', ciName: 'dc1-core-sw-01', status: 'Critical', severity: 'Critical' }),
      tile({ deviceId: 'device-2', ciName: 'dc1-core-sw-02', status: 'Ok' }),
    ]))
    renderBoard('/monitoring?status=Critical')

    expect(await screen.findByText('dc1-core-sw-01')).toBeInTheDocument()
    expect(screen.queryByText('dc1-core-sw-02')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Critical/ })).toHaveAttribute('aria-pressed', 'true')
  })

  it('ignores a deep link asking for a status that does not exist', async () => {
    vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board([
      tile({ deviceId: 'device-1', ciName: 'dc1-core-sw-01', status: 'Ok' }),
    ]))
    renderBoard('/monitoring?status=OnFire')

    expect(await screen.findByText('dc1-core-sw-01')).toBeInTheDocument()
  })

  it('draws a tile per device with the worst thing wrong with it', async () => {
    vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board([
      tile(),
      tile({
        deviceId: 'device-2', ciName: 'dc1-core-rtr-01', address: '10.40.0.2', status: 'Critical',
        severity: 'Critical', openAlerts: 2, criticalAlerts: 1, warningAlerts: 1,
        headline: 'Host is unreachable', worstAlertRaisedAt: '2026-08-11T11:00:00Z',
      }),
    ]))

    renderBoard()

    expect(await screen.findByText('dc1-core-sw-01')).toBeInTheDocument()
    expect(screen.getByText('Host is unreachable')).toBeInTheDocument()
    expect(screen.getByText('2 open alerts')).toBeInTheDocument()
  })

  /**
   * A device nobody has heard from is not a healthy device — the tile says so rather than reading
   * green on the strength of no evidence.
   */
  it('says a device has never reported rather than calling it healthy', async () => {
    vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board([
      tile({ status: 'Unknown', lastTelemetryAt: null }),
    ]))

    renderBoard()

    expect(await screen.findByText('Not yet reported')).toBeInTheDocument()
    expect(screen.getByText('no readings yet')).toBeInTheDocument()
  })

  /** An acknowledgement says somebody is on it, not that the device is any better. */
  it('keeps a tile critical while showing that its alerts are acknowledged', async () => {
    vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board([
      tile({ status: 'Critical', severity: 'Critical', openAlerts: 1, criticalAlerts: 1, acknowledgedAlerts: 1, headline: 'Host is unreachable' }),
    ]))

    renderBoard()

    expect(await screen.findByText('Critical')).toBeInTheDocument()
    expect(screen.getByText('1 acknowledged')).toBeInTheDocument()
  })

  it('narrows the wall to one status when its count tile is pressed', async () => {
    const user = userEvent.setup()
    vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board([
      tile(),
      tile({ deviceId: 'device-2', ciName: 'dc1-core-rtr-01', status: 'Critical', severity: 'Critical', openAlerts: 1, criticalAlerts: 1 }),
    ]))

    renderBoard()
    await screen.findByText('dc1-core-sw-01')
    await user.click(screen.getByRole('button', { name: /^Critical/ }))

    expect(screen.queryByText('dc1-core-sw-01')).not.toBeInTheDocument()
    expect(screen.getByText('dc1-core-rtr-01')).toBeInTheDocument()
  })

  it('offers an explanation rather than a bare empty board', async () => {
    vi.mocked(monitoringApi.statusBoard).mockResolvedValue(board([]))

    renderBoard()

    expect(await screen.findByText(/No devices are monitored yet/)).toBeInTheDocument()
  })
})
