import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { dashboardApi, type Dashboard, type DashboardWidget } from '../../api/dashboard'
import { DashboardPage } from './DashboardPage'

vi.mock('../../api/dashboard', async (original) => {
  const actual = await original<typeof import('../../api/dashboard')>()
  return {
    ...actual,
    dashboardApi: {
      ...actual.dashboardApi,
      get: vi.fn(),
      createView: vi.fn(),
      saveView: vi.fn(),
      selectView: vi.fn(),
      deleteView: vi.fn(),
    },
  }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

function widget(overrides: Partial<DashboardWidget> = {}): DashboardWidget {
  return {
    type: 'SlaHealth',
    status: 'Loaded',
    title: 'SLA health',
    subtitle: '12 open tickets against an SLA target',
    headline: 2,
    headlineLabel: 'Breaching now',
    headlineTone: 'Critical',
    segments: [
      { label: 'Breached', value: 2, tone: 'Critical', link: null },
      { label: 'At risk', value: 3, tone: 'Warning', link: null },
    ],
    rows: [
      {
        title: 'VPN is down for the Leeds office',
        subtitle: 'INC-000042 · High',
        badge: 'Breached by 3h',
        tone: 'Critical',
        link: { target: 'Ticket', filter: null, recordId: 'ticket-1' },
        at: null,
      },
    ],
    rowTotal: 5,
    rowsTruncated: true,
    link: { target: 'TicketList', filter: null, recordId: null },
    ...overrides,
  }
}

const networkStatus = widget({
  type: 'NetworkStatus',
  title: 'Network status',
  subtitle: '18 monitored devices',
  headline: 1,
  headlineLabel: 'Critical now',
  headlineTone: 'Critical',
  segments: [
    {
      label: 'Critical',
      value: 1,
      tone: 'Critical',
      link: { target: 'DeviceList', filter: 'Critical', recordId: null },
    },
  ],
  rows: [],
  rowTotal: 0,
  rowsTruncated: false,
  link: { target: 'DeviceList', filter: null, recordId: null },
})

const licences = widget({
  type: 'LicenseCompliance',
  title: 'Licence compliance',
  subtitle: '31 catalogued products',
  headline: 0,
  headlineLabel: 'Over-deployed',
  headlineTone: 'Ok',
  segments: [{ label: 'Over-deployed', value: 0, tone: 'Critical', link: null }],
  rows: [],
  rowTotal: 0,
  rowsTruncated: false,
  link: { target: 'SoftwareCompliance', filter: null, recordId: null },
})

function dashboard(overrides: Partial<Dashboard> = {}): Dashboard {
  return {
    layout: {
      source: 'RoleDefault',
      viewId: null,
      name: null,
      preset: 'Executive',
      savedAt: null,
      placements: [
        { type: 'SlaHealth', width: 'Third', display: 'Card' },
        { type: 'NetworkStatus', width: 'Third', display: 'Card' },
      ],
    },
    views: [],
    widgets: [widget(), networkStatus, licences],
    ...overrides,
  }
}

/** A view of this person's own, active, holding both cards. */
function saved(overrides: Partial<Dashboard> = {}): Dashboard {
  return dashboard({
    layout: {
      source: 'Saved',
      viewId: 'view-1',
      name: 'Morning check',
      preset: 'Executive',
      savedAt: '2026-08-17T08:00:00Z',
      placements: [
        { type: 'SlaHealth', width: 'Third', display: 'Card' },
        { type: 'NetworkStatus', width: 'Third', display: 'Card' },
      ],
    },
    views: [{ id: 'view-1', name: 'Morning check', isActive: true, updatedAt: '2026-08-17T08:00:00Z' }],
    ...overrides,
  })
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}>
    <MemoryRouter><DashboardPage /></MemoryRouter>
  </QueryClientProvider>)
}

const viewState = {
  layout: saved().layout,
  views: saved().views,
}

describe('DashboardPage', () => {
  beforeEach(() => {
    vi.mocked(dashboardApi.get).mockReset()
    vi.mocked(dashboardApi.createView).mockReset()
    vi.mocked(dashboardApi.saveView).mockReset()
    vi.mocked(dashboardApi.selectView).mockReset()
    vi.mocked(dashboardApi.deleteView).mockReset()
    vi.mocked(dashboardApi.get).mockResolvedValue(dashboard())
    vi.mocked(dashboardApi.createView).mockResolvedValue(viewState)
    vi.mocked(dashboardApi.saveView).mockResolvedValue(viewState)
    vi.mocked(dashboardApi.selectView).mockResolvedValue(viewState)
    vi.mocked(dashboardApi.deleteView).mockResolvedValue(viewState)
  })

  it('draws the widgets the layout places, in its order', async () => {
    renderPage()

    const cards = await screen.findAllByRole('article')
    expect(cards.map((card) => within(card).getByRole('heading').textContent))
      .toEqual(['SLA health', 'Network status'])
    expect(screen.getByText('12 open tickets against an SLA target')).toBeInTheDocument()
    expect(screen.getByText('Breaching now')).toBeInTheDocument()
  })

  it('deep-links every band and every row into the list it counts', async () => {
    renderPage()

    // The WP's third verification step, from the browser's side: the server sends a target and a filter
    // and this is where they become a route.
    expect(await screen.findByRole('link', { name: /Critical\s*1/ }))
      .toHaveAttribute('href', '/monitoring?status=Critical')
    expect(screen.getByRole('link', { name: /VPN is down/ })).toHaveAttribute('href', '/tickets/ticket-1')
    expect(screen.getByRole('link', { name: 'View tickets' })).toHaveAttribute('href', '/tickets')
    expect(screen.getByRole('link', { name: 'View devices' })).toHaveAttribute('href', '/monitoring')
  })

  it('states the honest total when a widget shows only some of its rows', async () => {
    renderPage()

    expect(await screen.findByText('Showing 1 of 5')).toBeInTheDocument()
  })

  it('says a failed widget could not be loaded rather than drawing it as zero', async () => {
    vi.mocked(dashboardApi.get).mockResolvedValue(dashboard({
      widgets: [
        widget({ status: 'Failed', segments: [], rows: [], headline: null, subtitle: null }),
        networkStatus,
        licences,
      ],
    }))
    renderPage()

    expect(await screen.findByText(/could not be loaded/)).toBeInTheDocument()
    // And the rest of the page is untouched: one module being unreachable must not blank the others.
    expect(screen.getByText('18 monitored devices')).toBeInTheDocument()
  })

  it('drops a widget this account may not read instead of rendering it empty', async () => {
    vi.mocked(dashboardApi.get).mockResolvedValue(dashboard({
      widgets: [widget(), { ...networkStatus, status: 'NotPermitted' }, licences],
    }))
    renderPage()

    await screen.findByRole('heading', { name: 'SLA health' })
    expect(screen.queryByRole('heading', { name: 'Network status' })).not.toBeInTheDocument()
  })

  it('offers an explanation rather than an empty grid when nothing is visible', async () => {
    vi.mocked(dashboardApi.get).mockResolvedValue({
      layout: {
        source: 'RoleDefault', viewId: null, name: null, preset: 'Operations', savedAt: null, placements: [],
      },
      views: [],
      widgets: [{ ...networkStatus, status: 'NotPermitted' }],
    })
    renderPage()

    expect(await screen.findByText('There are no widgets for your account')).toBeInTheDocument()
  })

  describe('views', () => {
    it('names the role default when this person has saved nothing', async () => {
      renderPage()

      expect(await screen.findByText('Executive default')).toBeInTheDocument()
    })

    it('lists the saved views as tabs and marks the active one', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved({
        views: [
          { id: 'view-1', name: 'Morning check', isActive: true, updatedAt: '2026-08-17T08:00:00Z' },
          { id: 'view-2', name: 'Licences', isActive: false, updatedAt: '2026-08-16T08:00:00Z' },
        ],
      }))
      renderPage()

      expect(await screen.findByRole('tab', { name: 'Morning check' }))
        .toHaveAttribute('aria-selected', 'true')
      expect(screen.getByRole('tab', { name: 'Licences' })).toHaveAttribute('aria-selected', 'false')
    })

    it('switches to another view when its tab is clicked', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved({
        views: [
          { id: 'view-1', name: 'Morning check', isActive: true, updatedAt: '2026-08-17T08:00:00Z' },
          { id: 'view-2', name: 'Licences', isActive: false, updatedAt: '2026-08-16T08:00:00Z' },
        ],
      }))
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('tab', { name: 'Licences' }))

      await waitFor(() => expect(dashboardApi.selectView).toHaveBeenCalledWith('view-2'))
    })

    /** The blank slate the rework asked for: a view created from the toolbar starts with no cards at all. */
    it('creates a blank view when one is made from the toolbar', async () => {
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: 'New view' }))
      await user.type(screen.getByLabelText('Name'), 'Night shift')
      await user.click(screen.getByRole('button', { name: 'Create view' }))

      await waitFor(() => expect(dashboardApi.createView).toHaveBeenCalledWith('Night shift', []))
    })

    /**
     * Saving while on the role default has nothing to write to, so it creates a view — and it keeps the
     * arrangement the person just made rather than throwing it away and starting them blank.
     */
    it('turns a rearranged default into a named view, keeping the arrangement', async () => {
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: /Network status/ }))
      await user.click(screen.getByRole('menuitem', { name: /Move earlier/ }))
      await user.click(screen.getByRole('button', { name: 'Save' }))
      await user.type(screen.getByLabelText('Name'), 'Mine')
      await user.click(screen.getByRole('button', { name: 'Create view' }))

      await waitFor(() => expect(dashboardApi.createView).toHaveBeenCalledWith('Mine', [
        { type: 'NetworkStatus', width: 'Third', display: 'Card' },
        { type: 'SlaHealth', width: 'Third', display: 'Card' },
      ]))
    })

    it('saves an existing view in place rather than making another one', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved())
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: /Network status/ }))
      await user.click(screen.getByRole('menuitem', { name: /Move earlier/ }))
      await user.click(screen.getByRole('button', { name: 'Save' }))

      await waitFor(() => expect(dashboardApi.saveView).toHaveBeenCalledWith('view-1', {
        placements: [
          { type: 'NetworkStatus', width: 'Third', display: 'Card' },
          { type: 'SlaHealth', width: 'Third', display: 'Card' },
        ],
      }))
      expect(dashboardApi.createView).not.toHaveBeenCalled()
    })

    it('deletes the view on screen', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved())
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: 'Delete Morning check' }))

      await waitFor(() => expect(dashboardApi.deleteView).toHaveBeenCalledWith('view-1'))
    })

    it('has no delete button on the role default, which is not a view', async () => {
      renderPage()

      await screen.findByText('Executive default')
      expect(screen.queryByRole('button', { name: /^Delete/ })).not.toBeInTheDocument()
    })

    it('offers to add a card that is not on the board, and puts it on the end', async () => {
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: 'Add card' }))
      await user.click(screen.getByRole('menuitem', { name: 'Licence compliance' }))

      const cards = screen.getAllByRole('article')
      expect(cards.map((card) => within(card).getByRole('heading').textContent))
        .toEqual(['SLA health', 'Network status', 'Licence compliance'])
      expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
    })

    it('says a view is empty rather than showing a bare grid', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved({
        layout: { ...saved().layout, placements: [] },
      }))
      renderPage()

      expect(await screen.findByText('This view is empty')).toBeInTheDocument()
    })
  })

  describe('the card menu', () => {
    it('swaps the card for another widget from its own title', async () => {
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: /SLA health/ }))
      await user.click(screen.getByRole('menuitemradio', { name: /Licence compliance/ }))

      const cards = screen.getAllByRole('article')
      expect(cards.map((card) => within(card).getByRole('heading').textContent))
        .toEqual(['Licence compliance', 'Network status'])
    })

    /** Choosing a widget that is already on the board swaps the two rather than drawing it twice. */
    it('swaps two placed cards around instead of duplicating one', async () => {
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: /SLA health/ }))
      await user.click(screen.getByRole('menuitemradio', { name: /Network status/ }))

      const cards = screen.getAllByRole('article')
      expect(cards.map((card) => within(card).getByRole('heading').textContent))
        .toEqual(['Network status', 'SLA health'])
    })

    it('changes a card\'s width from the menu', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved())
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: /SLA health/ }))
      await user.click(screen.getByRole('menuitemradio', { name: 'Full width' }))
      await user.click(screen.getByRole('button', { name: 'Save' }))

      await waitFor(() => expect(dashboardApi.saveView).toHaveBeenCalledWith('view-1', {
        placements: [
          { type: 'SlaHealth', width: 'Full', display: 'Card' },
          { type: 'NetworkStatus', width: 'Third', display: 'Card' },
        ],
      }))
    })

    it('removes a card from the view', async () => {
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: /SLA health/ }))
      await user.click(screen.getByRole('menuitem', { name: /Remove from view/ }))

      expect(screen.queryByRole('heading', { name: 'SLA health' })).not.toBeInTheDocument()
      expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
    })
  })

  describe('cards or charts', () => {
    /**
     * The chart itself is Recharts inside a ResponsiveContainer, which measures the DOM and therefore draws
     * nothing in a browser-less test. What is asserted here is everything around it that is this app's: the
     * choice being offered, the values staying readable, and the fallback when there is nothing to plot.
     */
    it('offers a shape for the card and remembers the one chosen', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved())
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: /SLA health/ }))
      await user.click(screen.getByRole('menuitemradio', { name: 'Donut' }))
      await user.click(screen.getByRole('button', { name: 'Save' }))

      await waitFor(() => expect(dashboardApi.saveView).toHaveBeenCalledWith('view-1', {
        placements: [
          { type: 'SlaHealth', width: 'Third', display: 'Donut' },
          { type: 'NetworkStatus', width: 'Third', display: 'Card' },
        ],
      }))
    })

    it('keeps the numbers readable when a card is drawn as a chart', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved({
        layout: {
          ...saved().layout,
          placements: [
            { type: 'SlaHealth', width: 'Third', display: 'Donut' },
            { type: 'NetworkStatus', width: 'Third', display: 'Card' },
          ],
        },
      }))
      renderPage()

      // The legend under the picture is the only copy a screen reader is given, so it carries the bands
      // and their deep links rather than the chart doing it.
      const values = await screen.findByRole('list', { name: 'SLA health values' })
      expect(within(values).getByText('Breached')).toBeInTheDocument()
      expect(within(values).getByText('At risk')).toBeInTheDocument()
    })

    it('will not offer a chart for a widget with nothing to plot', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved({
        widgets: [widget({ segments: [] }), networkStatus, licences],
      }))
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: /SLA health/ }))

      // Disabled rather than hidden: a control that vanishes reads as a feature that broke.
      expect(screen.getByRole('menuitemradio', { name: 'Donut' })).toBeDisabled()
      expect(screen.getByRole('menuitemradio', { name: 'Bars' })).toBeDisabled()
    })

    it('draws a card when the saved shape can no longer be drawn', async () => {
      // A view saved while the widget had bands, read back after they have gone.
      vi.mocked(dashboardApi.get).mockResolvedValue(saved({
        layout: {
          ...saved().layout,
          placements: [{ type: 'SlaHealth', width: 'Third', display: 'Donut' }],
        },
        widgets: [widget({ segments: [], headline: 4, headlineLabel: 'Breaching now' }), networkStatus, licences],
      }))
      renderPage()

      expect(await screen.findByText('Breaching now')).toBeInTheDocument()
      expect(screen.queryByText('Nothing to chart yet.')).not.toBeInTheDocument()
    })

    it('says a chart has nothing to show rather than drawing a ring of nothing', async () => {
      vi.mocked(dashboardApi.get).mockResolvedValue(saved({
        layout: {
          ...saved().layout,
          placements: [{ type: 'LicenseCompliance', width: 'Third', display: 'Donut' }],
        },
      }))
      renderPage()

      // Licence compliance in this fixture has one band, at nought.
      expect(await screen.findByText('Nothing to chart yet.')).toBeInTheDocument()
    })
  })

  describe('dragging', () => {
    /**
     * The drag the first cut of this feature shipped broken. Two things were wrong and both are asserted
     * here: nothing was ever put on the transfer, which Firefox requires before it will start a drag at
     * all, and the card was full of anchors — natively draggable — so a grab usually started a link drag
     * instead.
     */
    it('reorders the cards when one is dropped on another', async () => {
      const user = userEvent.setup()
      renderPage()

      await user.click(await screen.findByRole('button', { name: 'Arrange' }))

      const [first, second] = screen.getAllByRole('article')
      const dataTransfer = { setData: vi.fn(), getData: vi.fn(), effectAllowed: '', dropEffect: '' }
      fireEvent.dragStart(second, { dataTransfer })
      fireEvent.dragOver(first, { dataTransfer })
      fireEvent.drop(first, { dataTransfer })

      // The payload Firefox insists on, and the effect Chrome needs for the cursor to say "move".
      expect(dataTransfer.setData).toHaveBeenCalledWith('text/plain', 'NetworkStatus')
      expect(dataTransfer.effectAllowed).toBe('move')

      const cards = screen.getAllByRole('article')
      expect(cards.map((card) => within(card).getByRole('heading').textContent))
        .toEqual(['Network status', 'SLA health'])
      expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
    })

    it('does not drag while the board is not being arranged', async () => {
      renderPage()

      const [, second] = await screen.findAllByRole('article')
      expect(second).not.toHaveAttribute('draggable', 'true')
    })
  })

  it('discards an unsaved rearrangement', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole('button', { name: /SLA health/ }))
    await user.click(screen.getByRole('menuitem', { name: /Remove from view/ }))
    await user.click(screen.getByRole('button', { name: 'Discard' }))

    const cards = screen.getAllByRole('article')
    expect(cards.map((card) => within(card).getByRole('heading').textContent))
      .toEqual(['SLA health', 'Network status'])
    expect(dashboardApi.saveView).not.toHaveBeenCalled()
  })

  it('will not save a layout nobody has changed', async () => {
    renderPage()

    await screen.findByRole('heading', { name: 'SLA health' })
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
  })

  it('says the dashboard could not be read rather than showing an empty one', async () => {
    // A failed read is a fact about the request; an empty dashboard is a claim about the estate. The two
    // must not look the same (WP-2.11's rule).
    vi.mocked(dashboardApi.get).mockRejectedValue(new Error('unreachable'))
    renderPage()

    expect(await screen.findByRole('alert')).toHaveTextContent('The dashboard could not be loaded.')
  })
})
