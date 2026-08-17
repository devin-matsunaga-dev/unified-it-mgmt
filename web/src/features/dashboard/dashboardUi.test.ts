import { describe, expect, it } from 'vitest'
import type { Dashboard, DashboardPlacement, DashboardWidget } from '../../api/dashboard'
import { searchResultHref } from '../search/searchUi'
import {
  addWidget,
  dashboardHref,
  dashboardLinkLabel,
  describeRowTruncation,
  effectiveDisplay,
  layoutsEqual,
  moveWidget,
  placedWidgets,
  removeWidget,
  replaceWidget,
  resizeWidget,
  segmentShares,
  setDisplay,
  setWidth,
  supportsChart,
  unplacedWidgets,
  widgetIcon,
  widths,
} from './dashboardUi'

describe('dashboardHref', () => {
  it('sends a record link through the same map the search box uses', () => {
    // The point of the assertion is the *sameness*, not the string: a second copy of this map is how a
    // widget and the search box start disagreeing about where an alert opens (WP-5.4's warning).
    expect(dashboardHref({ target: 'Alert', filter: null, recordId: 'alert-1' }))
      .toBe(searchResultHref({ type: 'Alert', id: 'alert-1' }))
    expect(dashboardHref({ target: 'Ticket', filter: null, recordId: 'ticket-1' }))
      .toBe(searchResultHref({ type: 'Ticket', id: 'ticket-1' }))
  })

  it('turns a filtered-list target into the query parameter that page reads', () => {
    expect(dashboardHref({ target: 'TicketList', filter: 'Critical', recordId: null }))
      .toBe('/tickets?priority=Critical')
    expect(dashboardHref({ target: 'DeviceList', filter: 'Critical', recordId: null }))
      .toBe('/monitoring?status=Critical')
    expect(dashboardHref({ target: 'AlertList', filter: 'Critical', recordId: null }))
      .toBe('/monitoring/alerts?severity=Critical')
    expect(dashboardHref({ target: 'SoftwareCompliance', filter: 'OverDeployed', recordId: null }))
      .toBe('/software?compliance=OverDeployed')
  })

  it('drops the filter when there is none, rather than narrowing to an empty string', () => {
    expect(dashboardHref({ target: 'TicketList', filter: null, recordId: null })).toBe('/tickets')
    expect(dashboardHref({ target: 'SoftwareCompliance', filter: null, recordId: null })).toBe('/software')
  })

  it('is null for a record link with no record, so nothing renders a link to nowhere', () => {
    expect(dashboardHref({ target: 'Ticket', filter: null, recordId: null })).toBeNull()
    expect(dashboardHref(null)).toBeNull()
  })

  it('names the destination it leads to', () => {
    expect(dashboardLinkLabel({ target: 'DeviceList', filter: null, recordId: null })).toBe('View devices')
    expect(dashboardLinkLabel(null)).toBe('View all')
  })
})

describe('moveWidget', () => {
  const layout: DashboardPlacement[] = [
    { type: 'SlaHealth', width: 'Half', display: 'Card' },
    { type: 'NetworkStatus', width: 'Half', display: 'Card' },
    { type: 'LicenseCompliance', width: 'Full', display: 'Card' },
  ]

  it('moves a card to the position asked for and shuffles the rest along', () => {
    expect(moveWidget(layout, 2, 0).map((placement) => placement.type))
      .toEqual(['LicenseCompliance', 'SlaHealth', 'NetworkStatus'])
  })

  it('clamps at both ends rather than wrapping', () => {
    // Deliberately unlike the search dropdown, which wraps: a layout is being edited, and a card that
    // leapt from the top of the page to the bottom would be a change to undo rather than one meant.
    expect(moveWidget(layout, 0, -1)).toEqual(layout)
    expect(moveWidget(layout, 2, 9)).toEqual(layout)
  })

  it('leaves the layout alone when the index is not one of its cards', () => {
    expect(moveWidget(layout, 7, 0)).toEqual(layout)
  })
})

describe('resizeWidget', () => {
  it('widens and narrows one step at a time, and only the card asked for', () => {
    const layout: DashboardPlacement[] = [
      { type: 'SlaHealth', width: 'Half', display: 'Card' },
      { type: 'NetworkStatus', width: 'Half', display: 'Card' },
    ]

    expect(resizeWidget(layout, 0, 1)[0].width).toBe('TwoThirds')
    expect(resizeWidget(layout, 0, 1)[1].width).toBe('Half')
    expect(resizeWidget(layout, 0, -1)[0].width).toBe('Third')
  })

  it('clamps at the narrowest and the widest', () => {
    const narrow: DashboardPlacement[] = [{ type: 'SlaHealth', width: widths[0], display: 'Card' }]
    const wide: DashboardPlacement[] = [{ type: 'SlaHealth', width: widths[widths.length - 1], display: 'Card' }]

    expect(resizeWidget(narrow, 0, -1)).toEqual(narrow)
    expect(resizeWidget(wide, 0, 1)).toEqual(wide)
  })
})

describe('layoutsEqual', () => {
  it('is false when the order or a width differs, so Save knows there is something to save', () => {
    const left: DashboardPlacement[] = [
      { type: 'SlaHealth', width: 'Half', display: 'Card' },
      { type: 'NetworkStatus', width: 'Half', display: 'Card' },
    ]

    expect(layoutsEqual(left, [...left])).toBe(true)
    expect(layoutsEqual(left, [left[1], left[0]])).toBe(false)
    expect(layoutsEqual(left, [{ ...left[0], width: 'Full' }, left[1]])).toBe(false)
    // The shape counts too — it is saved with the placement, so a card switched to a donut is a change
    // worth keeping. Leaving it out here made Save stay greyed out after exactly that edit.
    expect(layoutsEqual(left, [{ ...left[0], display: 'Donut' }, left[1]])).toBe(false)
    expect(layoutsEqual(left, left.slice(1))).toBe(false)
  })
})

describe('placedWidgets', () => {
  function widget(overrides: Partial<DashboardWidget> = {}): DashboardWidget {
    return {
      type: 'SlaHealth', status: 'Loaded', title: 'SLA health', subtitle: null, headline: null,
      headlineLabel: null, headlineTone: 'Neutral', segments: [], rows: [], rowTotal: 0,
      rowsTruncated: false, link: null,
      ...overrides,
    }
  }

  function dashboard(widgets: DashboardWidget[], placements: DashboardPlacement[]): Dashboard {
    return {
      layout: {
        source: 'RoleDefault', viewId: null, name: null, preset: 'Operations', savedAt: null, placements,
      },
      views: [],
      widgets,
    }
  }

  it('draws the placed widgets in layout order', () => {
    const drawn = placedWidgets(dashboard(
      [widget(), widget({ type: 'NetworkStatus', title: 'Network status' })],
      [{ type: 'NetworkStatus', width: 'Half', display: 'Card' }, { type: 'SlaHealth', width: 'Half', display: 'Card' }],
    ))

    expect(drawn.map((entry) => entry.widget.type)).toEqual(['NetworkStatus', 'SlaHealth'])
  })

  it('drops a widget this account may not read rather than rendering it empty', () => {
    // WP-5.4's rule restated: "you may not read this" and "there is nothing to show" are different
    // answers, and only one of them is about the estate.
    const drawn = placedWidgets(dashboard(
      [widget({ type: 'LicenseCompliance', status: 'NotPermitted' })],
      [{ type: 'LicenseCompliance', width: 'Half', display: 'Card' }],
    ))

    expect(drawn).toEqual([])
  })

  it('keeps a widget whose query failed, because a failed card still has to say so', () => {
    const drawn = placedWidgets(dashboard(
      [widget({ status: 'Failed' })],
      [{ type: 'SlaHealth', width: 'Half', display: 'Card' }],
    ))

    expect(drawn).toHaveLength(1)
  })

  it('ignores a placement for a widget the server did not send', () => {
    const drawn = placedWidgets(dashboard([], [{ type: 'SlaHealth', width: 'Half', display: 'Card' }]))

    expect(drawn).toEqual([])
  })
})

describe('describeRowTruncation', () => {
  it('states the honest total when rows were left out, and says nothing when they were not', () => {
    const base: DashboardWidget = {
      type: 'SlaHealth', status: 'Loaded', title: 'SLA health', subtitle: null, headline: null,
      headlineLabel: null, headlineTone: 'Neutral', segments: [], rows: [], rowTotal: 0,
      rowsTruncated: false, link: null,
    }

    expect(describeRowTruncation({
      ...base,
      rows: [{ title: 'One', subtitle: null, badge: null, tone: 'Critical', link: null, at: null }],
      rowTotal: 41,
      rowsTruncated: true,
    })).toBe('Showing 1 of 41')
    expect(describeRowTruncation(base)).toBeNull()
  })
})

describe('replaceWidget', () => {
  const layout: DashboardPlacement[] = [
    { type: 'SlaHealth', width: 'Third', display: 'Card' },
    { type: 'NetworkStatus', width: 'Full', display: 'Card' },
  ]

  it('puts a different widget in the slot, keeping the width the slot had', () => {
    const next = replaceWidget(layout, 0, 'LicenseCompliance')

    expect(next.map((placement) => placement.type)).toEqual(['LicenseCompliance', 'NetworkStatus'])
    expect(next[0].width).toBe('Third')
  })

  it('swaps two cards when the chosen widget is already on the board', () => {
    // Never the same card twice with one silently dropped — and swapping is what somebody looking at two
    // cards and wanting them the other way round actually means.
    const next = replaceWidget(layout, 0, 'NetworkStatus')

    expect(next.map((placement) => placement.type)).toEqual(['NetworkStatus', 'SlaHealth'])
    expect(next[0].width).toBe('Third')
    expect(next[1].width).toBe('Full')
  })

  it('does nothing when the card already shows that widget', () => {
    expect(replaceWidget(layout, 0, 'SlaHealth')).toBe(layout)
  })
})

describe('addWidget and removeWidget', () => {
  const layout: DashboardPlacement[] = [{ type: 'SlaHealth', width: 'Third', display: 'Card' }]

  it('adds a card on the end at the default width', () => {
    expect(addWidget(layout, 'NetworkStatus')).toEqual([
      { type: 'SlaHealth', width: 'Third', display: 'Card' },
      { type: 'NetworkStatus', width: 'Third', display: 'Card' },
    ])
  })

  it('refuses to add a widget already on the board', () => {
    expect(addWidget(layout, 'SlaHealth')).toBe(layout)
  })

  it('removes exactly the card asked for', () => {
    expect(removeWidget([...layout, { type: 'NetworkStatus', width: 'Full', display: 'Card' }], 0))
      .toEqual([{ type: 'NetworkStatus', width: 'Full', display: 'Card' }])
  })

  it('can empty a view completely, which is a state the server allows', () => {
    expect(removeWidget(layout, 0)).toEqual([])
  })
})

describe('setWidth', () => {
  it('sets one card to an exact width and leaves the others', () => {
    const layout: DashboardPlacement[] = [
      { type: 'SlaHealth', width: 'Third', display: 'Card' },
      { type: 'NetworkStatus', width: 'Third', display: 'Card' },
    ]

    expect(setWidth(layout, 1, 'Full')).toEqual([
      { type: 'SlaHealth', width: 'Third', display: 'Card' },
      { type: 'NetworkStatus', width: 'Full', display: 'Card' },
    ])
  })
})

describe('segmentShares', () => {
  it('turns the bands into percentages that add up, dropping the empty ones', () => {
    const shares = segmentShares([
      { label: 'Breached', value: 1, tone: 'Critical' },
      { label: 'At risk', value: 1, tone: 'Warning' },
      { label: 'On track', value: 2, tone: 'Ok' },
      { label: 'Nothing', value: 0, tone: 'Neutral' },
    ])

    expect(shares?.map((share) => share.label)).toEqual(['Breached', 'At risk', 'On track'])
    expect(shares?.map((share) => share.percent)).toEqual([25, 25, 50])
  })

  it('is null when every band is zero, so nothing draws a bar out of nothing', () => {
    // Five equal slices would be a lie about the estate and a flat grey line would be noise; the card
    // says so in words instead.
    expect(segmentShares([{ label: 'Breached', value: 0, tone: 'Critical' }])).toBeNull()
    expect(segmentShares([])).toBeNull()
  })
})

describe('unplacedWidgets', () => {
  function widget(type: DashboardWidget['type'], status: DashboardWidget['status'] = 'Loaded'): DashboardWidget {
    return {
      type, status, title: type, subtitle: null, headline: null, headlineLabel: null,
      headlineTone: 'Neutral', segments: [], rows: [], rowTotal: 0, rowsTruncated: false, link: null,
    }
  }

  const dashboard: Dashboard = {
    layout: {
      source: 'Saved', viewId: 'view-1', name: 'Mine', preset: 'Operations', savedAt: null, placements: [],
    },
    views: [{ id: 'view-1', name: 'Mine', isActive: true, updatedAt: '2026-08-17T09:00:00Z' }],
    widgets: [widget('SlaHealth'), widget('NetworkStatus'), widget('LicenseCompliance', 'NotPermitted')],
  }

  it('offers what is not on the board and never what this account may not read', () => {
    // Offering a card that would refuse to load is worse than not offering it.
    expect(unplacedWidgets(dashboard, [{ type: 'SlaHealth', width: 'Third', display: 'Card' }]).map((item) => item.type))
      .toEqual(['NetworkStatus'])
  })

  it('offers everything when the view is blank', () => {
    expect(unplacedWidgets(dashboard, []).map((item) => item.type))
      .toEqual(['SlaHealth', 'NetworkStatus'])
  })
})

describe('the display shapes', () => {
  function widget(overrides: Partial<DashboardWidget> = {}): DashboardWidget {
    return {
      type: 'SlaHealth', status: 'Loaded', title: 'SLA health', subtitle: null, headline: null,
      headlineLabel: null, headlineTone: 'Neutral',
      segments: [{ label: 'Breached', value: 2, tone: 'Critical', link: null }],
      rows: [], rowTotal: 0, rowsTruncated: false, link: null,
      ...overrides,
    }
  }

  it('can chart a widget that reports bands', () => {
    expect(supportsChart(widget())).toBe(true)
  })

  it('cannot chart a widget that reports only rows', () => {
    // The recent-root-causes card is exactly this: a list of what explained what, with nothing to plot.
    expect(supportsChart(widget({ segments: [] }))).toBe(false)
  })

  it('cannot chart a widget whose query failed, because there is nothing to believe', () => {
    expect(supportsChart(widget({ status: 'Failed', segments: [] }))).toBe(false)
  })

  it('falls back to a card when the shape asked for cannot be drawn', () => {
    // A view saved while a widget had bands must not break when it stops having them.
    expect(effectiveDisplay(widget({ segments: [] }), 'Donut')).toBe('Card')
    expect(effectiveDisplay(widget(), 'Donut')).toBe('Donut')
    expect(effectiveDisplay(widget(), 'Card')).toBe('Card')
  })

  it('sets one card\'s shape and leaves the others', () => {
    const layout: DashboardPlacement[] = [
      { type: 'SlaHealth', width: 'Third', display: 'Card' },
      { type: 'NetworkStatus', width: 'Third', display: 'Card' },
    ]

    expect(setDisplay(layout, 1, 'Bar')).toEqual([
      { type: 'SlaHealth', width: 'Third', display: 'Card' },
      { type: 'NetworkStatus', width: 'Third', display: 'Bar' },
    ])
  })

  it('gives a card added from the menu the card shape', () => {
    expect(addWidget([], 'SlaHealth')).toEqual([{ type: 'SlaHealth', width: 'Third', display: 'Card' }])
  })
})

describe('widgetIcon', () => {
  it('has an icon for a widget it has never heard of', () => {
    // The property that keeps "adding a widget is a registration" true: an unknown widget still draws,
    // with a generic icon, rather than needing this app released alongside it.
    expect(widgetIcon('SomethingNew' as DashboardWidget['type'])).toBeTruthy()
  })
})
