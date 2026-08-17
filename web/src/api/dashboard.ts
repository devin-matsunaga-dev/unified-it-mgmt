import { apiRequest } from './client'

/**
 * The unified dashboard (WP-5.5). Every widget arrives in one shape, so a widget this app has never heard
 * of still renders — which is what makes adding one on the server a registration rather than a release of
 * both halves.
 */
export type DashboardWidgetType =
  | 'SlaHealth'
  | 'OpenByPriority'
  | 'NetworkStatus'
  | 'LicenseCompliance'
  | 'RecentRootCauses'

/** The span of the twelve-column grid a widget occupies. The name maps to a fraction of a row. */
export type DashboardWidgetWidth = 'Third' | 'Half' | 'TwoThirds' | 'Full'

/**
 * Why a widget shows no numbers, which is three different statements and never one.
 *
 * - `Loaded` — it ran; a zero is a fact about the estate.
 * - `NotPermitted` — this signed-in user may not read that data at all. Dropped rather than rendered.
 * - `Failed` — its query failed. Says so; a number that could not be read is not zero.
 */
export type DashboardWidgetStatus = 'Loaded' | 'NotPermitted' | 'Failed'

/** The semantic weight of a number, in the platform's vocabulary. DESIGN §3 owns what each one looks like. */
export type DashboardTone = 'Neutral' | 'Ok' | 'Warning' | 'Critical' | 'Info'

export type DashboardLinkTarget =
  | 'TicketList'
  | 'Ticket'
  | 'DeviceList'
  | 'AlertList'
  | 'Alert'
  | 'SoftwareCompliance'

/**
 * Where a number or a row leads, named in domain terms. Turning one into a route is this app's job and
 * happens in exactly one place — see `dashboardUi.ts`.
 */
export type DashboardLink = {
  target: DashboardLinkTarget
  /** The value to narrow the destination to, in the domain's spelling: `Critical`, `OverDeployed`. */
  filter: string | null
  /** The record to open, for the two targets that name one. */
  recordId: string | null
}

export type DashboardSegment = {
  label: string
  value: number
  tone: DashboardTone
  link: DashboardLink | null
}

export type DashboardRow = {
  title: string
  subtitle: string | null
  badge: string | null
  tone: DashboardTone
  link: DashboardLink | null
  /** When it happened, as an instant. Rendered in the reader's own time zone (DESIGN §10). */
  at: string | null
}

export type DashboardWidget = {
  type: DashboardWidgetType
  status: DashboardWidgetStatus
  title: string
  subtitle: string | null
  headline: number | null
  headlineLabel: string | null
  /** Whether the headline is good news — the widget's judgement, this app's colour. */
  headlineTone: DashboardTone
  segments: DashboardSegment[]
  rows: DashboardRow[]
  /** Everything the rows were taken from, cap or no cap — so five of forty-one says forty-one. */
  rowTotal: number
  rowsTruncated: boolean
  link: DashboardLink | null
}

/**
 * How a card draws what it was given. A presentation choice, which is why it is saved with the placement
 * rather than sent with the widget's data.
 *
 * The chart shapes read a widget's segments and nothing else, so a widget that reports only rows cannot be
 * drawn as one — `supportsChart` works that out from the payload.
 */
export type DashboardDisplay = 'Card' | 'Donut' | 'Bar'

export type DashboardPlacement = {
  type: DashboardWidgetType
  width: DashboardWidgetWidth
  display: DashboardDisplay
}

export type DashboardLayout = {
  /** `Saved` while a view of this person's is on screen; `RoleDefault` until they make one. */
  source: 'RoleDefault' | 'Saved'
  /** The view being drawn, or null for the role default — which is not a view and cannot be edited. */
  viewId: string | null
  name: string | null
  /** Which default they started from — what they see with no views at all. */
  preset: 'Operations' | 'Executive'
  savedAt: string | null
  placements: DashboardPlacement[]
}

/** One saved view, as the tab bar lists them. */
export type DashboardViewSummary = {
  id: string
  name: string
  isActive: boolean
  updatedAt: string
}

export type Dashboard = {
  layout: DashboardLayout
  views: DashboardViewSummary[]
  widgets: DashboardWidget[]
}

/** What every write answers with: the views that now exist, and the layout now on screen. */
export type DashboardViewState = { layout: DashboardLayout; views: DashboardViewSummary[] }

export const dashboardApi = {
  get: () => apiRequest<Dashboard>('/api/dashboard'),
  createView: (name: string, placements: DashboardPlacement[]) =>
    apiRequest<DashboardViewState>('/api/dashboard/views', {
      method: 'POST',
      body: JSON.stringify({ name, placements }),
    }),
  /** A null `placements` leaves the cards alone, which is how a rename travels. */
  saveView: (viewId: string, body: { name?: string; placements?: DashboardPlacement[] }) =>
    apiRequest<DashboardViewState>(`/api/dashboard/views/${viewId}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  selectView: (viewId: string) =>
    apiRequest<DashboardViewState>(`/api/dashboard/views/${viewId}/selection`, { method: 'POST' }),
  deleteView: (viewId: string) =>
    apiRequest<DashboardViewState>(`/api/dashboard/views/${viewId}`, { method: 'DELETE' }),
}
