import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { ApiError } from '../../api/client'
import { assetsApi, discoveryApi, type CiTypeSchema, type DiscoveredDevice } from '../../api/assets'
import { DiscoveryReviewPage } from './DiscoveryReviewPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return {
    ...actual,
    assetsApi: { ...actual.assetsApi, listTypeSchemas: vi.fn() },
    discoveryApi: {
      listDiscovered: vi.fn(),
      getDiscovered: vi.fn(),
      approveDiscovered: vi.fn(),
      rejectDiscovered: vi.fn(),
      getCiDiscoveryFacts: vi.fn(),
    },
  }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const schemas: CiTypeSchema[] = [
  {
    type: 'NetworkDevice',
    attributes: [
      { key: 'managementIp', label: 'Management IP', kind: 'IpAddress', isRequired: true },
      { key: 'vendor', label: 'Vendor', kind: 'Text', isRequired: true },
      { key: 'portCount', label: 'Port count', kind: 'Integer', isRequired: true },
    ],
    customFields: [],
  },
  { type: 'Hardware', attributes: [{ key: 'manufacturer', label: 'Manufacturer', kind: 'Text', isRequired: true }], customFields: [] },
]

const stranger: DiscoveredDevice = {
  id: 'disc-1',
  identityKey: 'snmp:sim-switch-healthy',
  address: '172.18.0.7',
  hostname: 'sim-switch-healthy.example.test',
  respondedToPing: true,
  openPorts: [22],
  snmp: {
    sysName: 'sim-switch-healthy',
    sysDescription: 'IT Platform simulated switch, healthy profile',
    sysObjectId: '1.3.6.1.4.1.8072.3.2.10',
    sysLocation: 'Primary Data Centre',
    sysContact: null,
    uptimeSeconds: 5_184_000,
  },
  neighbours: [
    { protocol: 'lldp', localPort: 'GigabitEthernet0/1', remoteSystemName: 'dc1-core-rtr-01', remotePort: 'Gi0/24', remoteAddress: null },
  ],
  discoveryName: 'discovery-1',
  scanProfileId: 'profile-1',
  scanProfileName: 'Local subnet sweep',
  status: 'Pending',
  ciId: null,
  ciName: null,
  matchRule: 'None',
  contenders: [],
  suggestedType: 'NetworkDevice',
  suggestedName: 'sim-switch-healthy',
  suggestedAttributes: { managementIp: '172.18.0.7' },
  firstSeenAt: '2026-08-13T12:00:00Z',
  lastSeenAt: '2026-08-13T12:05:00Z',
  sightingCount: 2,
  reviewedBy: null,
  reviewedAt: null,
  reviewNote: null,
}

const ambiguous: DiscoveredDevice = {
  ...stranger,
  id: 'disc-2',
  address: '10.10.0.1',
  suggestedName: 'twin',
  matchRule: 'Ambiguous',
  contenders: [
    { ciId: 'ci-a', name: 'DC1 core router', type: 'NetworkDevice' },
    { ciId: 'ci-b', name: 'Decommissioned router', type: 'NetworkDevice' },
  ],
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><DiscoveryReviewPage /></QueryClientProvider></MemoryRouter>)
}

describe('DiscoveryReviewPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue(schemas)
  })

  it('opens on the pending queue and shows what the scan learned about each device', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({ items: [stranger], total: 1, page: 1, pageSize: 25 })

    renderPage()

    expect(await screen.findByText('sim-switch-healthy')).toBeInTheDocument()
    expect(screen.getByText(/172\.18\.0\.7/)).toBeInTheDocument()
    expect(screen.getByText('IT Platform simulated switch, healthy profile')).toBeInTheDocument()
    expect(screen.getByText(/dc1-core-rtr-01/)).toBeInTheDocument()
    expect(discoveryApi.listDiscovered).toHaveBeenCalledWith(expect.objectContaining({ status: 'Pending' }))
  })

  it('switches the queue to the ignore list when the tab is chosen', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })
    renderPage()
    await screen.findByRole('tab', { name: 'Ignored' })

    await userEvent.click(screen.getByRole('tab', { name: 'Ignored' }))

    await waitFor(() => expect(discoveryApi.listDiscovered).toHaveBeenCalledWith(expect.objectContaining({ status: 'Rejected' })))
  })

  it('approves a stranger with the attributes the scan could not know', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({ items: [stranger], total: 1, page: 1, pageSize: 25 })
    vi.mocked(discoveryApi.approveDiscovered).mockResolvedValue({ ...stranger, status: 'Approved', ciId: 'ci-new' })
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Approve' }))

    const dialog = await screen.findByRole('dialog', { name: 'Approve discovered device' })
    // The one attribute discovery observed is already filled in; the two it cannot know are blank.
    expect(within(dialog).getByLabelText(/Management IP/)).toHaveValue('172.18.0.7')
    expect(within(dialog).getByLabelText(/Vendor/)).toHaveValue('')

    await userEvent.type(within(dialog).getByLabelText(/Vendor/), 'Cisco')
    await userEvent.type(within(dialog).getByLabelText(/Port count/), '48')
    await userEvent.click(within(dialog).getByLabelText(/Also monitor it/))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Approve' }))

    await waitFor(() => expect(discoveryApi.approveDiscovered).toHaveBeenCalledWith('disc-1', expect.objectContaining({
      type: 'NetworkDevice',
      name: 'sim-switch-healthy',
      attributes: { managementIp: '172.18.0.7', vendor: 'Cisco', portCount: '48' },
      enrollMonitoring: true,
    })))
  })

  it('blocks an approval that leaves a required attribute blank, before it reaches the API', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({ items: [stranger], total: 1, page: 1, pageSize: 25 })
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog', { name: 'Approve discovered device' })

    await userEvent.click(within(dialog).getByRole('button', { name: 'Approve' }))

    expect(await within(dialog).findByText('Vendor is required.')).toBeInTheDocument()
    expect(discoveryApi.approveDiscovered).not.toHaveBeenCalled()
  })

  it('shows the server field errors beside their inputs when the API refuses the approval', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({ items: [stranger], total: 1, page: 1, pageSize: 25 })
    vi.mocked(discoveryApi.approveDiscovered).mockRejectedValue(
      new ApiError(400, 'Validation failed', { 'attributes.vendor': ['Vendor is required for a NetworkDevice CI.'] }))
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog', { name: 'Approve discovered device' })
    await userEvent.type(within(dialog).getByLabelText(/Vendor/), 'x')
    await userEvent.type(within(dialog).getByLabelText(/Port count/), '1')

    await userEvent.click(within(dialog).getByRole('button', { name: 'Approve' }))

    expect(await within(dialog).findByText('Vendor is required for a NetworkDevice CI.')).toBeInTheDocument()
  })

  it('offers the contenders rather than creating a CI when two claim the device', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({ items: [ambiguous], total: 1, page: 1, pageSize: 25 })
    vi.mocked(discoveryApi.approveDiscovered).mockResolvedValue({ ...ambiguous, status: 'Approved', ciId: 'ci-b' })
    renderPage()
    expect(await screen.findByText(/Two CIs claim this device/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog', { name: 'Approve discovered device' })
    await userEvent.selectOptions(within(dialog).getByLabelText(/Which one/), 'ci-b')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Approve' }))

    // No type and no attributes: this approval attaches to a CI that exists rather than creating one.
    await waitFor(() => expect(discoveryApi.approveDiscovered).toHaveBeenCalledWith('disc-2', expect.objectContaining({ ciId: 'ci-b' })))
    expect(vi.mocked(discoveryApi.approveDiscovered).mock.calls[0][1].type).toBeUndefined()
  })

  it('asks twice before ignoring a device, because the ignore list is what stops it coming back', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({ items: [stranger], total: 1, page: 1, pageSize: 25 })
    vi.mocked(discoveryApi.rejectDiscovered).mockResolvedValue({ ...stranger, status: 'Rejected' })
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Ignore' }))
    expect(discoveryApi.rejectDiscovered).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'Confirm ignore' }))
    await waitFor(() => expect(discoveryApi.rejectDiscovered).toHaveBeenCalledWith('disc-1', null))
  })

  it('offers no decision buttons on a card somebody has already settled', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({
      items: [{ ...stranger, status: 'Approved', ciId: 'ci-new', ciName: 'Core switch', matchRule: 'None', reviewedBy: 'technician1' }],
      total: 1, page: 1, pageSize: 25,
    })

    renderPage()

    expect(await screen.findByText('sim-switch-healthy')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Ignore' })).not.toBeInTheDocument()
  })

  it('explains an empty queue rather than printing "no data"', async () => {
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })

    renderPage()

    expect(await screen.findByText(/Every device the last scan found is already in the CMDB/)).toBeInTheDocument()
  })

  it('says an empty list of open ports is an ICMP-only answer, not a device that serves nothing', async () => {
    // A TCP fingerprint cannot see a UDP service: the simulator answers SNMP on 161 and reports no
    // open ports at all. WP-4.1's hand-verification recorded exactly this trap.
    vi.mocked(discoveryApi.listDiscovered).mockResolvedValue({
      items: [{ ...stranger, openPorts: [], respondedToPing: true }], total: 1, page: 1, pageSize: 25,
    })

    renderPage()

    expect(await screen.findByText('None answered (ICMP only)')).toBeInTheDocument()
  })
})
