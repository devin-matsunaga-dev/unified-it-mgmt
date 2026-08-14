import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { assetsApi, type CiTimeline, type CiTimelineEntry } from '../../api/assets'
import { CiTimelinePanel } from './CiTimelinePanel'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, getTimeline: vi.fn() } }
})

const entries: CiTimelineEntry[] = [
  {
    kind: 'Alert', id: 'alert-open', occurredAt: '2026-08-14T10:00:00Z',
    title: 'CPU above 90%', detail: '10.10.0.21 · still open · suppressed under its root cause',
    actor: null, severity: 'Critical', status: 'Open', priority: null,
    alertId: 'alert-open', deviceId: 'device-1', ticketId: null, ticketNumber: null, linkedAt: null,
  },
  {
    kind: 'Ticket', id: 'ticket-1', occurredAt: '2026-08-13T06:00:00Z',
    title: 'ERP is unreachable', detail: 'Incident', actor: 'Sam Roe',
    severity: null, status: 'In progress', priority: 'High',
    alertId: null, deviceId: null, ticketId: 'ticket-1', ticketNumber: 'INC-000042',
    linkedAt: '2026-08-13T12:00:00Z',
  },
  {
    kind: 'Alert', id: 'alert-cleared', occurredAt: '2026-08-11T02:00:00Z',
    title: 'No response to ICMP', detail: '10.10.0.21 · recovered after 25 minutes',
    actor: null, severity: 'Warning', status: 'Cleared', priority: null,
    alertId: 'alert-cleared', deviceId: 'device-1', ticketId: null, ticketNumber: null, linkedAt: null,
  },
  {
    kind: 'Lifecycle', id: 'transition-1', occurredAt: '2026-08-10T09:00:00Z',
    title: 'In stock → Deployed', detail: 'Racked in DC1.', actor: 'alex',
    severity: null, status: null, priority: null,
    alertId: null, deviceId: null, ticketId: null, ticketNumber: null, linkedAt: null,
  },
  {
    kind: 'Config', id: 'audit-1', occurredAt: '2026-08-09T14:00:00Z',
    title: 'Record updated', detail: 'Changed name, ownership.siteName.', actor: 'alex',
    severity: null, status: null, priority: null,
    alertId: null, deviceId: null, ticketId: null, ticketNumber: null, linkedAt: null,
  },
]

const timeline: CiTimeline = {
  ciId: 'ci-host',
  ciName: 'DC1 hypervisor host 1',
  from: null,
  to: null,
  limit: 50,
  kinds: ['Alert', 'Ticket', 'Lifecycle', 'Config'],
  summary: {
    entryCount: 5,
    totalCount: 5,
    truncated: false,
    earliestAt: '2026-08-09T14:00:00Z',
    latestAt: '2026-08-14T10:00:00Z',
  },
  sources: [
    { kind: 'Alert', requested: true, returned: 2, total: 2, truncated: false },
    { kind: 'Ticket', requested: true, returned: 1, total: 1, truncated: false },
    { kind: 'Lifecycle', requested: true, returned: 1, total: 1, truncated: false },
    { kind: 'Config', requested: true, returned: 1, total: 1, truncated: false },
  ],
  entries,
}

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}>
    <CiTimelinePanel ciId="ci-host" />
  </QueryClientProvider></MemoryRouter>)
}

describe('CiTimelinePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.getTimeline).mockResolvedValue(timeline)
  })

  /**
   * The WP's own verification: mixed events, in order, on one axis. Read off the rendered text rather
   * than off the fixture, because the thing that could break is the rendering re-ordering what the
   * server sorted.
   */
  it('renders every kind of event in the order the server gave them', async () => {
    const { container } = renderPanel()
    await screen.findByText('CPU above 90%')

    const text = container.textContent ?? ''
    const order = [
      'CPU above 90%',            // alert, newest
      'ERP is unreachable',       // ticket
      'No response to ICMP',      // alert, long since recovered
      'In stock → Deployed',      // lifecycle
      'Record updated',           // config, oldest
    ]
    const positions = order.map((title) => text.indexOf(title))

    expect(positions.filter((at) => at < 0)).toEqual([])
    expect(positions).toEqual([...positions].sort((a, b) => a - b))
  })

  /**
   * The axis is grouped under day headings, and every event lands in exactly one of them.
   *
   * The number of groups is deliberately not asserted: the headings are local days, so a fixture
   * spanning five UTC days is four or five local ones depending on where the suite runs. Which day each
   * entry belongs to is settled TZ-independently in `timeline.test.ts`; what this proves is that the
   * grouping is rendered and loses nothing.
   */
  it('groups the axis into days without dropping an event', async () => {
    renderPanel()
    await screen.findByText('CPU above 90%')

    const days = within(screen.getByRole('list', { name: 'Timeline by day' })).getAllByRole('list')
    expect(days.length).toBeGreaterThan(1)
    expect(days.reduce((total, day) => total + within(day).getAllByRole('listitem').length, 0))
      .toBe(entries.length)
  })

  it('states how much of the history is on screen', async () => {
    renderPanel()

    expect(await screen.findByText('5 events, most recent first')).toBeInTheDocument()
  })

  /**
   * The filter is a server request, not a rendering trick. Filtering here would make "alerts only" show
   * the newest fifty *events* rather than the newest fifty alerts.
   */
  it('asks the server for one kind when that filter is chosen', async () => {
    renderPanel()
    await screen.findByText('CPU above 90%')

    await userEvent.click(screen.getByRole('button', { name: 'Alerts' }))

    await waitFor(() => expect(assetsApi.getTimeline)
      .toHaveBeenCalledWith('ci-host', { types: ['Alert'] }))
  })

  it('starts unfiltered, with "All" pressed and nothing else', async () => {
    renderPanel()
    await screen.findByText('CPU above 90%')

    expect(screen.getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'Alerts' })).toHaveAttribute('aria-pressed', 'false')
    expect(assetsApi.getTimeline).toHaveBeenCalledWith('ci-host', { types: [] })
  })

  it('links a ticket entry to its ticket and an alert entry to the alert board', async () => {
    renderPanel()

    const ticket = await screen.findByRole('link', { name: /ERP is unreachable/ })
    expect(ticket).toHaveAttribute('href', '/tickets/ticket-1')
    expect(screen.getByRole('link', { name: 'CPU above 90%' }))
      .toHaveAttribute('href', '/monitoring/alerts?alertId=alert-open')
  })

  /** WP-5.1: an alert nobody was told about is still something that happened to this machine. */
  it('shows a suppressed alert and says why nobody was told', async () => {
    renderPanel()

    expect(await screen.findByText(/suppressed under its root cause/)).toBeInTheDocument()
  })

  it('says when a ticket was attached to the asset later than it was raised', async () => {
    renderPanel()

    expect(await screen.findByText(/linked to this asset/)).toBeInTheDocument()
  })

  /**
   * The misreading this screen is most exposed to. An asset that has never alerted, seen through the
   * alerts filter, must not read as an asset nothing has ever happened to.
   */
  it('distinguishes an empty filter result from an asset with no history', async () => {
    vi.mocked(assetsApi.getTimeline).mockResolvedValue({
      ...timeline,
      kinds: ['Alert'],
      summary: { entryCount: 0, totalCount: 0, truncated: false, earliestAt: null, latestAt: null },
      sources: timeline.sources.map((source) => source.kind === 'Alert'
        ? { ...source, returned: 0, total: 0 }
        : { ...source, requested: false, returned: 0, total: 0 }),
      entries: [],
    })
    renderPanel()
    await screen.findByText('Nothing has been recorded against this asset yet')
    expect(screen.getByText(/Alerts, tickets, lifecycle moves and record edits all land here/))
      .toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Alerts' }))

    expect(await screen.findByText('Nothing of the kinds you selected')).toBeInTheDocument()
    expect(screen.getByText(/Choose "All" to see the rest/)).toBeInTheDocument()
  })

  /** A capped source has to say so per source: "the alerts are cut" sends an operator somewhere else. */
  it('names the source that ran out of room and what it is holding back', async () => {
    vi.mocked(assetsApi.getTimeline).mockResolvedValue({
      ...timeline,
      summary: { ...timeline.summary, entryCount: 5, totalCount: 404, truncated: true },
      sources: timeline.sources.map((source) => source.kind === 'Alert'
        ? { ...source, returned: 50, total: 400, truncated: true }
        : source),
    })
    renderPanel()

    expect(await screen.findByText(/alerts: showing 50 of 400/)).toBeInTheDocument()
    expect(screen.getByText('5 events of 404, most recent first')).toBeInTheDocument()
  })

  /**
   * A failed read is a fact about the request and must never render as an asset with no history — the
   * WP-2.11 rule, which this panel is one of the easiest places in the app to break.
   */
  it('says the timeline could not be loaded rather than showing an empty one', async () => {
    vi.mocked(assetsApi.getTimeline).mockRejectedValue(new Error('boom'))
    renderPanel()

    const alert = await screen.findByRole('alert')
    expect(within(alert).getByText('The timeline could not be loaded.')).toBeInTheDocument()
    expect(screen.queryByText(/Nothing has been recorded/)).not.toBeInTheDocument()
    expect(screen.queryByText(/all land here/)).not.toBeInTheDocument()
  })

  it('shows a skeleton rather than a spinner while it loads', () => {
    vi.mocked(assetsApi.getTimeline).mockReturnValue(new Promise(() => {}))
    renderPanel()

    expect(screen.getByLabelText('Loading timeline')).toBeInTheDocument()
  })
})
