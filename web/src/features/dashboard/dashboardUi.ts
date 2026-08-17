import {
  AppWindow,
  ChartNoAxesColumn,
  Headphones,
  MonitorCog,
  ShieldAlert,
  Waypoints,
} from 'lucide-react'
import type {
  Dashboard,
  DashboardDisplay,
  DashboardLink,
  DashboardPlacement,
  DashboardTone,
  DashboardWidget,
  DashboardWidgetType,
  DashboardWidgetWidth,
} from '../../api/dashboard'
import { searchResultHref } from '../search/searchUi'

/**
 * Everything about the dashboard that is a decision rather than a rendering (WP-5.5). Kept out of the
 * components so the keyboard's idea of "move this card left" and the grid's idea of where it ends up come
 * from one function.
 */

/**
 * Where a widget's number or row leads.
 *
 * Two kinds of destination live here, and only one of them is new. A link at a **record** — a ticket, an
 * alert — is handed to `searchResultHref`, which WP-5.4 made the one place that turns a kind and an id into
 * a path; a second copy is how a widget and the search box start disagreeing about where an alert opens.
 * What is decided here is the **filtered lists**, which search has no opinion about: the server sends the
 * filter in the domain's spelling and this turns it into the query parameter each page reads.
 */
export function dashboardHref(link: DashboardLink | null | undefined): string | null {
  if (!link) return null
  switch (link.target) {
    case 'Ticket':
      return link.recordId ? searchResultHref({ type: 'Ticket', id: link.recordId }) : null
    case 'Alert':
      return link.recordId ? searchResultHref({ type: 'Alert', id: link.recordId }) : null
    case 'TicketList':
      return link.filter ? `/tickets?priority=${encodeURIComponent(link.filter)}` : '/tickets'
    case 'DeviceList':
      return link.filter ? `/monitoring?status=${encodeURIComponent(link.filter)}` : '/monitoring'
    case 'AlertList':
      return link.filter ? `/monitoring/alerts?severity=${encodeURIComponent(link.filter)}` : '/monitoring/alerts'
    case 'SoftwareCompliance':
      return link.filter ? `/software?compliance=${encodeURIComponent(link.filter)}` : '/software'
    default:
      return null
  }
}

/** What the link under a widget's heading says, so a card names its own destination. */
export function dashboardLinkLabel(link: DashboardLink | null | undefined): string {
  switch (link?.target) {
    case 'TicketList':
      return 'View tickets'
    case 'DeviceList':
      return 'View devices'
    case 'AlertList':
      return 'View alerts'
    case 'SoftwareCompliance':
      return 'View compliance'
    default:
      return 'View all'
  }
}

/**
 * The icon in a card's header.
 *
 * A partial map with a fallback rather than an exhaustive one, deliberately: a widget the server adds later
 * still renders, with a generic chart icon, instead of failing to draw or forcing an SPA release. That is
 * the same property the server-composed title and tones protect — the icon is the one piece of a card this
 * app can improve on afterwards without the widget having asked.
 */
const widgetIcons: Partial<Record<DashboardWidgetType, typeof ChartNoAxesColumn>> = {
  SlaHealth: ShieldAlert,
  OpenByPriority: Headphones,
  NetworkStatus: MonitorCog,
  LicenseCompliance: AppWindow,
  RecentRootCauses: Waypoints,
}

export function widgetIcon(type: DashboardWidgetType) {
  return widgetIcons[type] ?? ChartNoAxesColumn
}

/**
 * DESIGN §3's semantic colours, one map for every widget. The server sends the meaning and this decides
 * what it looks like — so a widget added later needs no entry of its own here.
 */
export const toneText: Record<DashboardTone, string> = {
  Critical: 'text-red-600 dark:text-red-400',
  Warning: 'text-amber-600 dark:text-amber-400',
  Ok: 'text-green-600 dark:text-green-400',
  Info: 'text-blue-600 dark:text-blue-400',
  Neutral: 'text-slate-900 dark:text-slate-100',
}

export const tonePill: Record<DashboardTone, string> = {
  Critical: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-300',
  Warning: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300',
  Ok: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-300',
  Info: 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-300',
  Neutral: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
}

/** The soft-tinted circle a card's icon sits in (DESIGN §6's KPI stat card). */
export const toneTint: Record<DashboardTone, string> = {
  Critical: 'bg-red-50 text-red-600 dark:bg-red-500/15 dark:text-red-400',
  Warning: 'bg-amber-50 text-amber-600 dark:bg-amber-500/15 dark:text-amber-400',
  Ok: 'bg-green-50 text-green-600 dark:bg-green-500/15 dark:text-green-400',
  Info: 'bg-blue-50 text-blue-600 dark:bg-blue-500/15 dark:text-blue-400',
  Neutral: 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400',
}

/**
 * The same five meanings as literal colours, for the charts.
 *
 * Recharts paints with values rather than classes, so this is the one place DESIGN §3's semantic hexes are
 * written down for it — the strong values, which §8 keeps unchanged in dark mode, so one map serves both.
 */
export const toneHex: Record<DashboardTone, string> = {
  Critical: '#DC2626',
  Warning: '#D97706',
  Ok: '#16A34A',
  Info: '#2563EB',
  Neutral: '#64748B',
}

export const toneDot: Record<DashboardTone, string> = {
  Critical: 'bg-red-500',
  Warning: 'bg-amber-500',
  Ok: 'bg-green-500',
  Info: 'bg-blue-500',
  Neutral: 'bg-slate-300 dark:bg-slate-600',
}

/**
 * The span of the twelve-column grid a width occupies. Written out rather than composed, because Tailwind
 * scans source text for class names — a template literal would produce classes that never reach the bundle.
 */
export const widthClass: Record<DashboardWidgetWidth, string> = {
  Third: 'md:col-span-6 xl:col-span-4',
  Half: 'md:col-span-6',
  TwoThirds: 'md:col-span-6 xl:col-span-8',
  Full: 'md:col-span-12',
}

/** Narrowest first, so "narrow" and "widen" are opposite directions through one list. */
export const widths: DashboardWidgetWidth[] = ['Third', 'Half', 'TwoThirds', 'Full']

export const widthLabel: Record<DashboardWidgetWidth, string> = {
  Third: 'One third',
  Half: 'Half',
  TwoThirds: 'Two thirds',
  Full: 'Full width',
}

/** The width a card added from the menu arrives at: wide enough to read, narrow enough to sit beside one. */
export const defaultWidth: DashboardWidgetWidth = 'Third'

export const displays: DashboardDisplay[] = ['Card', 'Donut', 'Bar']

export const displayLabel: Record<DashboardDisplay, string> = {
  Card: 'Card',
  Donut: 'Donut',
  Bar: 'Bars',
}

/**
 * Whether a widget can be drawn as a chart at all.
 *
 * Derived from the payload rather than declared by the widget: the charts read segments and nothing else,
 * so a widget that reports only rows — the recent root causes, say — has nothing to plot. Deriving it means
 * a widget added later gets the shapes for free without having to ask for them, and one whose bands all
 * disappear stops offering them rather than drawing an empty ring.
 */
export function supportsChart(widget: DashboardWidget): boolean {
  return widget.status === 'Loaded' && widget.segments.length > 0
}

/**
 * The display actually used. A card that cannot be charted falls back to being a card rather than drawing
 * nothing — a view saved when a widget had bands must not break when it stops having them.
 */
export function effectiveDisplay(
  widget: DashboardWidget,
  display: DashboardDisplay,
): DashboardDisplay {
  return display === 'Card' || supportsChart(widget) ? display : 'Card'
}

/** Sets one card's shape, which is what the card menu offers beside its width. */
export function setDisplay(
  placements: DashboardPlacement[],
  index: number,
  display: DashboardDisplay,
): DashboardPlacement[] {
  if (!placements[index]) return placements
  return placements.map((item, position) => position === index ? { ...item, display } : item)
}

/**
 * Moves a card, clamping at both ends.
 *
 * Clamping rather than wrapping — the opposite of the search results list, deliberately. A dropdown is a
 * short cycle a reader is walking through; a layout is a thing being edited, and a card that leaps from the
 * top of the page to the bottom because somebody pressed the arrow once too often is a change they have to
 * undo rather than one they meant.
 */
export function moveWidget(
  placements: DashboardPlacement[],
  from: number,
  to: number,
): DashboardPlacement[] {
  if (from < 0 || from >= placements.length) return placements
  const target = Math.max(0, Math.min(placements.length - 1, to))
  if (target === from) return placements
  const next = [...placements]
  const [moved] = next.splice(from, 1)
  next.splice(target, 0, moved)
  return next
}

/** Resizes one card, clamping at the narrowest and widest. */
export function resizeWidget(
  placements: DashboardPlacement[],
  index: number,
  step: 1 | -1,
): DashboardPlacement[] {
  const placement = placements[index]
  if (!placement) return placements
  const current = widths.indexOf(placement.width)
  const target = Math.max(0, Math.min(widths.length - 1, current + step))
  if (target === current) return placements
  return placements.map((item, position) =>
    position === index ? { ...item, width: widths[target] } : item)
}

/** Sets one card to an exact width, which is what the card menu offers. */
export function setWidth(
  placements: DashboardPlacement[],
  index: number,
  width: DashboardWidgetWidth,
): DashboardPlacement[] {
  if (!placements[index]) return placements
  return placements.map((item, position) => position === index ? { ...item, width } : item)
}

/**
 * Puts a different widget in one slot.
 *
 * If the chosen widget is already on the board the two **swap places**, rather than the board ending up
 * with the same card twice and one silently dropped. Swapping is also what somebody means by it: they are
 * looking at two cards and want them the other way round.
 */
export function replaceWidget(
  placements: DashboardPlacement[],
  index: number,
  type: DashboardWidgetType,
): DashboardPlacement[] {
  const placement = placements[index]
  if (!placement || placement.type === type) return placements

  const existing = placements.findIndex((item) => item.type === type)
  if (existing < 0) {
    return placements.map((item, position) => position === index ? { ...item, type } : item)
  }

  return placements.map((item, position) =>
    position === index ? { ...item, type }
      : position === existing ? { ...item, type: placement.type }
        : item)
}

/** Takes a card off the board. The widget is still offered by the menus; it just is not placed. */
export function removeWidget(placements: DashboardPlacement[], index: number): DashboardPlacement[] {
  return placements.filter((_, position) => position !== index)
}

/** Puts a widget on the end of the board at the default width. */
export function addWidget(
  placements: DashboardPlacement[],
  type: DashboardWidgetType,
): DashboardPlacement[] {
  return placements.some((placement) => placement.type === type)
    ? placements
    : [...placements, { type, width: defaultWidth, display: 'Card' }]
}

/**
 * The widgets somebody may put on a card: everything the server let them see, whether or not it is placed.
 * A `NotPermitted` widget never appears — offering a card that would refuse to load is worse than not
 * offering it.
 */
export function offerableWidgets(dashboard: Dashboard): DashboardWidget[] {
  return dashboard.widgets.filter((widget) => widget.status !== 'NotPermitted')
}

/** The widgets nothing on the board is showing, which is what "Add card" lists. */
export function unplacedWidgets(
  dashboard: Dashboard,
  placements: DashboardPlacement[],
): DashboardWidget[] {
  const placed = new Set(placements.map((placement) => placement.type))
  return offerableWidgets(dashboard).filter((widget) => !placed.has(widget.type))
}

/**
 * Two layouts are the same when the same widgets sit in the same order, at the same widths, drawn as the
 * same shapes. **Every member of a placement has to be compared here**: this is what decides whether Save
 * is offered, so a member left out is a change somebody makes and then silently cannot keep.
 */
export function layoutsEqual(left: DashboardPlacement[], right: DashboardPlacement[]): boolean {
  return left.length === right.length
    && left.every((placement, index) =>
      placement.type === right[index].type
      && placement.width === right[index].width
      && placement.display === right[index].display)
}

/**
 * The widgets to draw, in layout order.
 *
 * A widget the layout does not place is not drawn, and one the caller may not read is dropped rather than
 * rendered with an explanation — WP-5.4's rule, restated: the response stays honest for anything else that
 * reads it while the screen stays about what this person actually has.
 */
export function placedWidgets(dashboard: Dashboard): { placement: DashboardPlacement; widget: DashboardWidget }[] {
  const byType = new Map(dashboard.widgets.map((widget) => [widget.type, widget]))
  return dashboard.layout.placements
    .map((placement) => ({ placement, widget: byType.get(placement.type) }))
    .filter((entry): entry is { placement: DashboardPlacement; widget: DashboardWidget } =>
      entry.widget !== undefined && entry.widget.status !== 'NotPermitted')
}

/**
 * What the footer of a widget says about how much it left out. Null when it left nothing out — "showing 3
 * of 3" only invites a reader to wonder what they are missing.
 */
export function describeRowTruncation(widget: DashboardWidget): string | null {
  if (!widget.rowsTruncated) return null
  return `Showing ${widget.rows.length} of ${widget.rowTotal}`
}

/**
 * The proportions of the bar under a headline, as percentages that add to a hundred.
 *
 * Null when every band is zero: a bar drawn from nothing is either five equal slices, which is a lie about
 * the estate, or an empty grey line, which is noise. A card with nothing in it says so in words instead.
 */
export function segmentShares(
  segments: { label: string; value: number; tone: DashboardTone }[],
): { label: string; tone: DashboardTone; percent: number }[] | null {
  const total = segments.reduce((sum, segment) => sum + Math.max(0, segment.value), 0)
  if (total <= 0) return null
  return segments
    .filter((segment) => segment.value > 0)
    .map((segment) => ({
      label: segment.label,
      tone: segment.tone,
      percent: (Math.max(0, segment.value) / total) * 100,
    }))
}

/** A timestamp in the reader's own time zone, short form (DESIGN §10). */
export function formatLocal(value: string): string {
  return new Date(value).toLocaleString(undefined, {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
  })
}
