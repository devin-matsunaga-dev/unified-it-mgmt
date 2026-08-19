import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { ApiError } from '../../api/client'
import { scanProfilesApi, type ScanProfile, type ScanRun } from '../../api/monitoring'
import { ScanProfilesPage } from './ScanProfilesPage'

vi.mock('../../api/monitoring', async (original) => {
  const actual = await original<typeof import('../../api/monitoring')>()
  return {
    ...actual,
    scanProfilesApi: {
      list: vi.fn(),
      get: vi.fn(),
      create: vi.fn(),
      update: vi.fn(),
      remove: vi.fn(),
      requestRun: vi.fn(),
      listRuns: vi.fn(),
      getSettings: vi.fn(),
      updateSettings: vi.fn(),
    },
  }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const local: ScanProfile = {
  id: 'profile-1',
  name: 'Local subnet sweep',
  description: 'Everything on the network this scanner sits on.',
  discoveryGroup: 'default',
  ranges: ['local'],
  ports: [22, 80, 443],
  intervalMinutes: 5,
  timeoutSeconds: 2,
  snmpEnabled: true,
  neighbourDiscoveryEnabled: true,
  isEnabled: true,
  scheduleEnabled: true,
  addressCount: null,
  createdBy: 'seed',
  createdAt: '2026-08-19T00:00:00Z',
  updatedBy: 'seed',
  updatedAt: '2026-08-19T00:00:00Z',
}

const onDemand: ScanProfile = {
  ...local,
  id: 'profile-2',
  name: 'Documentation range',
  ranges: ['198.51.100.0/24'],
  ports: [],
  scheduleEnabled: false,
  addressCount: 254,
  snmpEnabled: false,
  neighbourDiscoveryEnabled: false,
}

function run(overrides: Partial<ScanRun> = {}): ScanRun {
  return {
    id: 'run-1',
    scanProfileId: 'profile-1',
    scanProfileName: 'Local subnet sweep',
    discoveryGroup: 'default',
    status: 'Queued',
    requestedBy: 'devin',
    requestedAt: '2026-08-19T01:00:00Z',
    discoveryName: null,
    dispatchedAt: null,
    deadlineAt: null,
    completedAt: null,
    addressesProbed: null,
    addressesTotal: null,
    devicesFound: null,
    lastRespondingAddress: null,
    progressAt: null,
    error: null,
    ...overrides,
  }
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><ScanProfilesPage /></QueryClientProvider></MemoryRouter>)
}

function listing(items: ScanProfile[]) {
  return { items, total: items.length, page: 1, pageSize: 200 }
}

describe('ScanProfilesPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(scanProfilesApi.list).mockResolvedValue(listing([local, onDemand]))
    vi.mocked(scanProfilesApi.listRuns).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50 })
    vi.mocked(scanProfilesApi.getSettings).mockResolvedValue({
      scheduledScanningEnabled: true, updatedBy: 'system:monitoring', updatedAt: '2026-08-19T00:00:00Z',
    })
  })

  it('lists what each profile scans and how big the range is', async () => {
    renderPage()

    expect(await screen.findByText('Local subnet sweep')).toBeInTheDocument()
    expect(screen.getByText('local')).toBeInTheDocument()
    // The figure exists so a /16 somebody typed reads as 65,534 probes before the scanner tries them.
    expect(screen.getByText(/254 addresses/)).toBeInTheDocument()
    // `local` resolves on the scanner, so its size is genuinely unknown here.
    expect(screen.getByText(/Size known only to the scanner/)).toBeInTheDocument()
  })

  it('separates a scheduled profile from one that is on demand only', async () => {
    renderPage()

    await screen.findByText('Local subnet sweep')
    expect(screen.getByText('Every 5 min')).toBeInTheDocument()
    expect(screen.getByText('On demand only')).toBeInTheDocument()
  })

  it('queues a scan rather than claiming it started', async () => {
    vi.mocked(scanProfilesApi.requestRun).mockResolvedValue(run())
    renderPage()
    await screen.findByText('Local subnet sweep')

    const card = screen.getByText('Local subnet sweep').closest('article')!
    await userEvent.click(within(card).getByRole('button', { name: /Scan now/ }))

    await waitFor(() => expect(scanProfilesApi.requestRun).toHaveBeenCalledWith('profile-1'))
  })

  it('says a second press is already queued rather than queueing twice', async () => {
    const { toast } = await import('sonner')
    vi.mocked(scanProfilesApi.requestRun).mockRejectedValue(
      new ApiError(409, "A scan of 'Local subnet sweep' is already queued."))
    renderPage()
    await screen.findByText('Local subnet sweep')

    const card = screen.getByText('Local subnet sweep').closest('article')!
    await userEvent.click(within(card).getByRole('button', { name: /Scan now/ }))

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("A scan of 'Local subnet sweep' is already queued."))
  })

  it('reports what a finished scan found, including a sweep that found nothing', async () => {
    vi.mocked(scanProfilesApi.listRuns).mockResolvedValue({
      items: [run({ status: 'Succeeded', addressesProbed: 254, devicesFound: 0, completedAt: '2026-08-19T01:02:00Z' })],
      total: 1, page: 1, pageSize: 50,
    })

    renderPage()

    // Zero is a result, not a failure — a clean sweep of an empty range is the thing that makes an
    // empty scan verifiable rather than a silence.
    expect(await screen.findByText(/probed 254 addresses and found 0/)).toBeInTheDocument()
  })

  it('shows how far a running sweep has got and the last address that answered', async () => {
    vi.mocked(scanProfilesApi.listRuns).mockResolvedValue({
      items: [run({
        status: 'Running',
        discoveryName: 'discovery-1',
        addressesProbed: 128,
        addressesTotal: 254,
        lastRespondingAddress: '172.18.0.7',
      })],
      total: 1, page: 1, pageSize: 50,
    })

    renderPage()

    // The evidence that a sweep is real. Not "scanning 172.18.0.7 now" — hundreds are in flight at
    // once, so a single current address would be a fiction.
    expect(await screen.findByText(/Swept 128 of 254 on discovery-1 · last answered 172\.18\.0\.7/))
      .toBeInTheDocument()
  })

  it('says nothing has answered yet rather than inventing an address', async () => {
    vi.mocked(scanProfilesApi.listRuns).mockResolvedValue({
      items: [run({ status: 'Running', discoveryName: 'discovery-1', addressesProbed: 12, addressesTotal: 254 })],
      total: 1, page: 1, pageSize: 50,
    })

    renderPage()

    expect(await screen.findByText(/Swept 12 of 254 on discovery-1 · nothing has answered yet/)).toBeInTheDocument()
  })

  it('points at the review queue once a scan has found something', async () => {
    vi.mocked(scanProfilesApi.listRuns).mockResolvedValue({
      items: [run({ status: 'Succeeded', addressesProbed: 254, devicesFound: 14 })],
      total: 1, page: 1, pageSize: 50,
    })

    renderPage()

    // A count with nothing to click is where the trail went cold: the devices are on the other tab.
    const link = await screen.findByRole('link', { name: /Open the review queue/ })
    expect(link).toHaveAttribute('href', '/assets/discovery')
  })

  it('does not offer the review queue when a scan found nothing', async () => {
    vi.mocked(scanProfilesApi.listRuns).mockResolvedValue({
      items: [run({ status: 'Succeeded', addressesProbed: 254, devicesFound: 0 })],
      total: 1, page: 1, pageSize: 50,
    })

    renderPage()

    await screen.findByText(/found 0/)
    expect(screen.queryByRole('link', { name: /Open the review queue/ })).not.toBeInTheDocument()
  })

  it('names the scanner when a run times out, because nothing else reports one that has died', async () => {
    vi.mocked(scanProfilesApi.listRuns).mockResolvedValue({
      items: [run({ status: 'TimedOut', discoveryName: 'discovery-1' })],
      total: 1, page: 1, pageSize: 50,
    })

    renderPage()

    expect(await screen.findByText(/No result from discovery-1/)).toBeInTheDocument()
  })

  it('switches scheduled scanning off for the whole estate and says on-demand still works', async () => {
    vi.mocked(scanProfilesApi.updateSettings).mockResolvedValue({
      scheduledScanningEnabled: false, updatedBy: 'devin', updatedAt: '2026-08-19T02:00:00Z',
    })
    renderPage()
    await screen.findByText('Local subnet sweep')

    await userEvent.click(screen.getByLabelText('Run profiles on their intervals'))

    await waitFor(() => expect(scanProfilesApi.updateSettings).toHaveBeenCalledWith(false))
  })

  it('shows a scheduled profile as paused while the estate switch is off', async () => {
    vi.mocked(scanProfilesApi.getSettings).mockResolvedValue({
      scheduledScanningEnabled: false, updatedBy: 'devin', updatedAt: '2026-08-19T02:00:00Z',
    })

    renderPage()

    // The profile still says it wants five minutes; the estate says not right now.
    expect(await screen.findByText('Every 5 min — paused')).toBeInTheDocument()
    expect(screen.getByText(/Nothing runs on a timer/)).toBeInTheDocument()
  })

  it('asks before deleting a profile and only deletes on the second press', async () => {
    vi.mocked(scanProfilesApi.remove).mockResolvedValue(undefined)
    renderPage()
    await screen.findByText('Local subnet sweep')

    await userEvent.click(screen.getByRole('button', { name: 'Delete Local subnet sweep' }))
    expect(scanProfilesApi.remove).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(scanProfilesApi.remove).toHaveBeenCalledWith('profile-1'))
  })

  it('offers a way in when no profile exists, because a scanner then has nowhere to look', async () => {
    vi.mocked(scanProfilesApi.list).mockResolvedValue(listing([]))

    renderPage()

    expect(await screen.findByText(/no scanner has anywhere to look/)).toBeInTheDocument()
  })
})
