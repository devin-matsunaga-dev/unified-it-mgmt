import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { assetsApi, type CiImpact } from '../../api/assets'
import { ApiError } from '../../api/client'
import { BlastRadiusPanel } from './BlastRadiusPanel'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, getImpact: vi.fn() } }
})

/**
 * One hypervisor host, two VMs racked on it and a service behind one of them — the shape of the seeded
 * estate's `dc1-esx-01`, which is the fixture the WP's verification step walks.
 */
const impact: CiImpact = {
  rootCiId: 'ci-host',
  rootCiName: 'DC1 hypervisor host 1',
  rootCiType: 'Server',
  maxDepth: 5,
  maxDepthReached: false,
  containsCycle: false,
  summary: {
    ciCount: 4,
    directCiCount: 2,
    openTicketCount: 2,
    breachedSlaCount: 1,
    atRiskSlaCount: 1,
    nextSlaDueAt: '2026-08-14T18:00:00Z',
    affectedUserCount: 2,
    affectedDepartmentCount: 2,
    cisWithoutDepartment: 1,
    cisTruncated: false,
    ticketsTruncated: false,
  },
  cis: [
    { ciId: 'ci-host', name: 'DC1 hypervisor host 1', type: 'Server', lifecycleState: 'Deployed', isActive: true, depth: 0, ownerUserId: 'user-1', ownerName: 'Alex Doe', departmentId: 'dept-it', departmentName: 'IT', siteName: 'Primary Data Centre', openTicketCount: 0 },
    { ciId: 'ci-vm-app', name: 'Finance ERP application server', type: 'Virtual', lifecycleState: 'Deployed', isActive: true, depth: 1, ownerUserId: 'user-1', ownerName: 'Alex Doe', departmentId: 'dept-fin', departmentName: 'Finance', siteName: 'Primary Data Centre', openTicketCount: 1 },
    { ciId: 'ci-vm-web', name: 'Customer portal web front end 1', type: 'Virtual', lifecycleState: 'Deployed', isActive: true, depth: 1, ownerUserId: 'user-2', ownerName: 'Sam Roe', departmentId: null, departmentName: null, siteName: 'Primary Data Centre', openTicketCount: 0 },
    { ciId: 'ci-svc', name: 'Finance reporting service', type: 'Logical', lifecycleState: 'Deployed', isActive: true, depth: 2, ownerUserId: 'user-2', ownerName: 'Sam Roe', departmentId: 'dept-fin', departmentName: 'Finance', siteName: 'Primary Data Centre', openTicketCount: 1 },
  ],
  tickets: [
    { ticketId: 'ticket-1', number: 'INC-000042', title: 'ERP is unreachable', status: 'InProgress', priority: 'Critical', createdAt: '2026-08-14T06:00:00Z', ciId: 'ci-vm-app', ciName: 'Finance ERP application server', sla: { policyName: 'Standard', resolutionDueAt: '2026-08-14T10:00:00Z', remainingSeconds: 0, breached: true, atRisk: false } },
    { ticketId: 'ticket-2', number: 'INC-000043', title: 'Month end reporting is late', status: 'Triage', priority: 'High', createdAt: '2026-08-14T08:00:00Z', ciId: 'ci-svc', ciName: 'Finance reporting service', sla: { policyName: 'Standard', resolutionDueAt: '2026-08-14T18:00:00Z', remainingSeconds: 7200, breached: false, atRisk: true } },
  ],
  departments: [
    { departmentId: 'dept-fin', name: 'Finance', ciCount: 2, openTicketCount: 2 },
    { departmentId: 'dept-it', name: 'IT', ciCount: 1, openTicketCount: 0 },
  ],
  users: [
    { userId: 'user-2', name: 'Sam Roe', ciCount: 2, openTicketCount: 1 },
    { userId: 'user-1', name: 'Alex Doe', ciCount: 2, openTicketCount: 1 },
  ],
}

function renderPanel(props: Partial<Parameters<typeof BlastRadiusPanel>[0]> = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}>
    <BlastRadiusPanel ciId="ci-host" {...props} />
  </QueryClientProvider></MemoryRouter>)
}

describe('BlastRadiusPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.getImpact).mockResolvedValue(impact)
  })

  /** The WP's own verification, on screen: the VMs, their tickets, and the departments that own them. */
  it('lists the affected CIs in rings, the open tickets, and the owning departments', async () => {
    renderPanel()

    expect(await screen.findByText('Directly dependent')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Finance ERP application server' })).toHaveAttribute('href', '/assets/ci-vm-app')
    expect(screen.getByRole('link', { name: 'Customer portal web front end 1' })).toBeInTheDocument()
    expect(screen.getByText('2 hops away')).toBeInTheDocument()

    expect(screen.getByText('ERP is unreachable')).toBeInTheDocument()

    const departments = screen.getByRole('heading', { name: 'Departments affected' }).parentElement!
    expect(within(departments).getByText('Finance').closest('li')).toHaveTextContent('2')
    expect(within(departments).getByText('IT')).toBeInTheDocument()
  })

  /**
   * The CI itself is part of its own outage and is labelled as the one being asked about, so a reader
   * never mistakes it for something that merely depends on it.
   */
  it('shows the CI itself at depth zero, marked as the current one', async () => {
    renderPanel()

    const ring = (await screen.findByText('This CI')).closest('li')!
    expect(within(ring).getByText('DC1 hypervisor host 1')).toBeInTheDocument()
    expect(within(ring).getByText('Current CI')).toBeInTheDocument()
    expect(within(ring).queryByRole('link', { name: 'DC1 hypervisor host 1' })).not.toBeInTheDocument()
  })

  it('leads with the numbers an operator triages on', async () => {
    renderPanel()

    expect(await screen.findByText('Affected CIs')).toBeInTheDocument()
    expect(screen.getByText('Affected CIs').parentElement).toHaveTextContent('4')
    // "SLA breached" is also the badge on the breached ticket, so the tile is read by its own label.
    expect(screen.getByText('Open tickets').parentElement).toHaveTextContent('2')
    expect(screen.getByText('People affected').parentElement).toHaveTextContent('2')
    expect(screen.getByText('3 other CIs affected · 2 directly', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('1 SLA breached · 1 at risk')).toBeInTheDocument()
  })

  it('marks the breached ticket and the one still in hand differently', async () => {
    renderPanel()

    const breached = (await screen.findByText('ERP is unreachable')).closest('li')!
    expect(within(breached).getByText('SLA breached')).toBeInTheDocument()

    const atRisk = screen.getByText('Month end reporting is late').closest('li')!
    expect(within(atRisk).getByText('At risk · 2h left')).toBeInTheDocument()
  })

  /** Counted, never bucketed under a department nobody recorded. */
  it('says how many affected CIs record no department', async () => {
    renderPanel()

    expect(await screen.findByText('1 affected CI records no department.')).toBeInTheDocument()
  })

  /**
   * A CI nothing depends on has a blast radius of exactly itself. An empty panel would read as a broken
   * one, so it says so in a sentence.
   */
  it('explains an empty radius rather than showing nothing', async () => {
    vi.mocked(assetsApi.getImpact).mockResolvedValue({
      ...impact,
      summary: { ...impact.summary, ciCount: 1, directCiCount: 0, openTicketCount: 0, breachedSlaCount: 0, atRiskSlaCount: 0 },
      cis: [impact.cis[0]],
      tickets: [],
    })
    renderPanel()

    expect(await screen.findByText(/Nothing recorded depends on this CI/)).toBeInTheDocument()
    expect(screen.queryByText('Directly dependent')).not.toBeInTheDocument()
  })

  /**
   * "Nothing depends on this" is a claim about the estate; a failed read is a fact about the request.
   * The two must not read the same (the WP-2.11 rule).
   */
  it('says the read failed rather than claiming the radius is empty', async () => {
    vi.mocked(assetsApi.getImpact).mockRejectedValue(new ApiError(500, 'The graph walk failed.'))
    renderPanel()

    expect(await screen.findByRole('alert')).toHaveTextContent('The blast radius could not be loaded.')
    expect(screen.queryByText(/Nothing recorded depends on this CI/)).not.toBeInTheDocument()
  })

  it('walks deeper on request, and only while the walk hit its own ceiling', async () => {
    const onDeeper = vi.fn()
    vi.mocked(assetsApi.getImpact).mockResolvedValue({ ...impact, maxDepthReached: true })
    renderPanel({ onDeeper })

    await userEvent.click(await screen.findByRole('button', { name: /Show deeper/ }))
    expect(onDeeper).toHaveBeenCalled()
  })

  it('offers no deeper walk when the graph already ended before the ceiling', async () => {
    renderPanel({ onDeeper: vi.fn() })

    await screen.findByText('Directly dependent')
    expect(screen.queryByRole('button', { name: /Show deeper/ })).not.toBeInTheDocument()
  })

  /**
   * The drawer variant is a peek: a 480px panel keeps the numbers and the worst rows, drops the
   * roll-ups, and links out to the asset page for the rest.
   */
  it('as the compact drawer variant, keeps the numbers and links out for the whole picture', async () => {
    renderPanel({ compact: true })

    expect(await screen.findByText('Affected CIs')).toBeInTheDocument()
    expect(screen.getByText('ERP is unreachable')).toBeInTheDocument()
    expect(screen.queryByText('Departments affected')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Open the full blast radius/ })).toHaveAttribute('href', '/assets/ci-host')
  })

  it('warns when the affected CIs depend on each other, so the count is not mistaken for a tree', async () => {
    vi.mocked(assetsApi.getImpact).mockResolvedValue({ ...impact, containsCycle: true })
    renderPanel()

    expect(await screen.findByText(/depend on each other/)).toBeInTheDocument()
  })

  it('asks the API for the depth it was given', async () => {
    renderPanel({ depth: 2 })

    await waitFor(() => expect(assetsApi.getImpact).toHaveBeenCalledWith('ci-host', 2))
  })
})
