namespace Platform.Dashboards;

/// <summary>
/// Every widget the platform can put on the unified dashboard, and the whole vocabulary a saved layout is
/// written in (WP-5.5).
/// <para>
/// Exactly one <see cref="IDashboardWidget"/> is registered per member, which
/// <c>DashboardApiIntegrationTests</c> pins against the real host — the same rule WP-5.4 put on
/// <c>SearchResultType</c>, and for a stronger reason here: a member with no widget behind it would leave a
/// hole in everybody's saved layout that nothing could ever fill.
/// </para>
/// <para>
/// The declaration order is the order a brand new widget is appended in for somebody who has already saved
/// a layout. It is deliberately <em>not</em> the default order of either role's dashboard — those are in
/// <see cref="DashboardDefaults"/>.
/// </para>
/// </summary>
public enum DashboardWidgetType
{
    /// <summary>Open tickets against their SLA targets. <c>helpdesk.ticket_slas</c>.</summary>
    SlaHealth = 1,

    /// <summary>Open tickets by priority. <c>helpdesk.tickets</c>.</summary>
    OpenByPriority = 2,

    /// <summary>The estate's device health tally. <c>monitoring.monitored_devices</c>.</summary>
    NetworkStatus = 3,

    /// <summary>Installed versus entitled, per product. <c>assets.software_products</c>.</summary>
    LicenseCompliance = 4,

    /// <summary>Alerts that explained other alerts. <c>monitoring.alerts</c>.</summary>
    RecentRootCauses = 5,
}

/// <summary>
/// How wide a widget sits, as a span of the twelve-column grid — so the value <em>is</em> the span and the
/// browser needs no second table to turn one into the other.
/// <para>
/// A closed set rather than a free integer, because DESIGN §5 allows four shapes (a third, a half, two
/// thirds, the full row) and an arbitrary width would produce rows that do not add up. Validated with
/// <c>Enum.IsDefined</c> at the edge: <c>TryParse</c> alone accepts any integer, which is the hole WP-5.3
/// found and WP-5.4 restated.
/// </para>
/// </summary>
public enum DashboardWidgetWidth
{
    Third = 4,
    Half = 6,
    TwoThirds = 8,
    Full = 12,
}

/// <summary>
/// How a card draws what it was given (WP-5.5). A presentation choice and nothing else: the widget sends
/// one payload and this decides what is made of it, which is why it lives on the <b>placement</b> — the
/// thing a person saves — rather than on the widget.
/// <para>
/// The chart shapes read a widget's <see cref="DashboardSegment"/>s and nothing else, so a widget that
/// reports only rows cannot be drawn as one. The browser works that out from the payload rather than being
/// told, because which shapes suit a set of numbers is a rendering question — and a widget added later then
/// gets the charts for free, without having to ask for them.
/// </para>
/// </summary>
public enum DashboardDisplay
{
    /// <summary>The default: a headline, a proportion bar, the bands listed, and any named rows.</summary>
    Card = 1,

    /// <summary>The bands as a ring, with the total in the middle (DESIGN §7).</summary>
    Donut = 2,

    /// <summary>The bands as bars, which is the shape to reach for when one band dwarfs the others.</summary>
    Bar = 3,
}

/// <summary>Why a widget shows no numbers, which is three different statements and never one.</summary>
public enum DashboardWidgetStatus
{
    /// <summary>It ran. A zero here is a fact about the estate.</summary>
    Loaded = 1,

    /// <summary>
    /// This actor may not read this kind of data at all. Distinct from a zero for WP-5.4's reason: an
    /// empty licence-compliance card would tell somebody the estate is fully licensed when the truth is a
    /// fact about their account. The browser drops these rather than rendering them.
    /// </summary>
    NotPermitted = 2,

    /// <summary>
    /// Its query failed. Kept as a fourth-of-five rather than failing the whole page: one module's schema
    /// being unreachable must not blank the other four widgets, and — WP-2.11's rule, which this repo has
    /// now applied to KPI counts, drift reports and status boards — a number that could not be read is not
    /// zero.
    /// </summary>
    Failed = 3,
}

/// <summary>
/// The semantic weight of a number or a row, in the platform's own vocabulary rather than in the browser's.
/// <para>
/// The colour is the SPA's (DESIGN §3 owns the hexes); which of the five meanings a number carries is the
/// module's, because only Helpdesk knows that a breached SLA is critical and only Assets knows that an
/// unlicensed product is worse than an unused entitlement. Sending a tone rather than a class is also what
/// keeps "adding a widget is a registration": a new widget needs no new entry in a browser-side map.
/// </para>
/// </summary>
public enum DashboardTone
{
    Neutral = 1,
    Ok = 2,
    Warning = 3,
    Critical = 4,
    Info = 5,
}

/// <summary>
/// Where a widget's number or row leads, named in domain terms so that routes stay the SPA's business.
/// <para>
/// WP-5.4 made the same split for search hits and left a warning with it: the map from a record to a route
/// lives in <c>web/src/features/search/searchUi.ts</c> and a second copy is how a widget and the search box
/// start disagreeing about where an alert opens. So the record targets here resolve through that same
/// function in the browser, and only the <em>filtered list</em> targets — which search has no opinion about
/// — are new.
/// </para>
/// </summary>
public enum DashboardLinkTarget
{
    /// <summary>The ticket list. <see cref="DashboardLink.Filter"/> is a priority name, or null for all.</summary>
    TicketList = 1,

    /// <summary>One ticket. <see cref="DashboardLink.RecordId"/>.</summary>
    Ticket = 2,

    /// <summary>The status board. <see cref="DashboardLink.Filter"/> is a device status name, or null.</summary>
    DeviceList = 3,

    /// <summary>The alert board. <see cref="DashboardLink.Filter"/> is a severity name, or null for all open.</summary>
    AlertList = 4,

    /// <summary>One alert, opening the board's existing drawer deep link. <see cref="DashboardLink.RecordId"/>.</summary>
    Alert = 5,

    /// <summary>
    /// The compliance report. <see cref="DashboardLink.Filter"/> is a <c>SoftwareComplianceState</c> name.
    /// </summary>
    SoftwareCompliance = 6,
}

/// <param name="Filter">
/// The value the destination should be narrowed to, in the domain's spelling (<c>Critical</c>,
/// <c>OverDeployed</c>). Null means the unfiltered list. The browser owns which query parameter it becomes.
/// </param>
/// <param name="RecordId">The record to open, for the two targets that name one. Null otherwise.</param>
public sealed record DashboardLink(DashboardLinkTarget Target, string? Filter = null, Guid? RecordId = null);

/// <summary>
/// One counted band of a widget — a priority, a severity, a compliance state. The number and the meaning
/// travel together, so a widget that is all counts needs no rows at all.
/// </summary>
public sealed record DashboardSegment(string Label, int Value, DashboardTone Tone, DashboardLink? Link = null);

/// <summary>
/// One named record a widget is pointing at: the ticket closest to breaching, the alert that explained six
/// others. Deliberately the same shape for every widget, so an unfamiliar widget still renders.
/// </summary>
/// <param name="Badge">The one status word this row is triaged by, already spelt for a reader.</param>
/// <param name="At">When it happened, as an instant — the browser converts to local time (DESIGN §10).</param>
public sealed record DashboardRow(
    string Title,
    string? Subtitle,
    string? Badge,
    DashboardTone Tone,
    DashboardLink? Link = null,
    DateTimeOffset? At = null);

/// <summary>
/// What one widget found. Composed by the module that owns the data, because only it can put the numbers
/// into a sentence a reader can act on.
/// </summary>
/// <param name="Headline">
/// The single number this widget is about, or null where the segments are the point. Kept beside them
/// rather than derived from them: "12 open" is not always the sum of the bands drawn under it.
/// </param>
/// <param name="RowTotal">
/// Everything the rows were taken from, cap or no cap — WP-2.4's rule that a truncated answer must never
/// look like a complete one.
/// </param>
/// <param name="HeadlineTone">
/// Whether the headline number is good news. The widget decides — only Helpdesk knows that a breached SLA
/// is critical while an open ticket is not — and the browser colours it, so a card is scannable at a glance
/// rather than five identical black numbers.
/// </param>
public sealed record DashboardWidgetData(
    string? Subtitle,
    int? Headline,
    string? HeadlineLabel,
    IReadOnlyList<DashboardSegment> Segments,
    IReadOnlyList<DashboardRow> Rows,
    int RowTotal,
    DashboardLink? Link = null,
    DashboardTone HeadlineTone = DashboardTone.Neutral)
{
    public bool RowsTruncated => RowTotal > Rows.Count;

    public static DashboardWidgetData Empty { get; } = new(null, null, null, [], [], 0);

    /// <summary>A headline nobody set has no tone to draw it in.</summary>
    public DashboardTone ToneOfHeadline => Headline is null ? DashboardTone.Neutral : HeadlineTone;
}

/// <summary>One widget as the browser receives it: what it is, whether it ran, and what it found.</summary>
public sealed record DashboardWidgetResponse(
    DashboardWidgetType Type,
    DashboardWidgetStatus Status,
    string Title,
    string? Subtitle,
    int? Headline,
    string? HeadlineLabel,
    DashboardTone HeadlineTone,
    IReadOnlyList<DashboardSegment> Segments,
    IReadOnlyList<DashboardRow> Rows,
    int RowTotal,
    bool RowsTruncated,
    DashboardLink? Link);

/// <summary>Where the layout on screen came from, which is what tells a reader what they are editing.</summary>
public enum DashboardLayoutSource
{
    /// <summary>This person has saved no view at all, so this is the default for their role.</summary>
    RoleDefault = 1,

    /// <summary>One of this person's own saved views.</summary>
    Saved = 2,
}

/// <summary>One widget's place in a layout: which widget, how wide, and drawn as what.</summary>
/// <param name="Display">
/// Defaulted so that a view stored before this existed reads as a card rather than as nothing — and so a
/// caller that does not care about the shape can leave it out.
/// </param>
public sealed record DashboardPlacement(
    DashboardWidgetType Type,
    DashboardWidgetWidth Width,
    DashboardDisplay Display = DashboardDisplay.Card);

/// <param name="ViewId">The saved view being drawn, or null while this is the untouched role default.</param>
/// <param name="Name">Its name, or null for the role default — which is not a view and cannot be edited.</param>
/// <param name="Preset">
/// Which default this person starts from — named so the screen can say "the executive default" rather than
/// leaving a manager to wonder why their dashboard is not a technician's.
/// </param>
public sealed record DashboardLayoutResponse(
    DashboardLayoutSource Source,
    Guid? ViewId,
    string? Name,
    DashboardPreset Preset,
    DateTimeOffset? SavedAt,
    IReadOnlyList<DashboardPlacement> Placements);

/// <summary>One of this person's saved views, as the tab bar lists them.</summary>
public sealed record DashboardViewSummary(Guid Id, string Name, bool IsActive, DateTimeOffset UpdatedAt);

/// <summary>What a write to a view did, or why it could not.</summary>
public enum DashboardViewOutcome
{
    Success = 1,

    /// <summary>Another view of this owner's already has that name. The tabs are read by name.</summary>
    NameInUse = 2,

    /// <summary>No such view belongs to this owner. Deliberately not distinguishable from somebody else's.</summary>
    NotFound = 3,

    /// <summary>This owner already holds as many views as they may.</summary>
    TooMany = 4,
}

/// <param name="Layout">The layout now on screen — the view just written, or what replaced a deleted one.</param>
public sealed record DashboardViewResult(
    DashboardViewOutcome Outcome,
    DashboardLayoutResponse? Layout = null,
    IReadOnlyList<DashboardViewSummary>? Views = null);

/// <summary>The two default layouts. Which one somebody opens on is decided by role and nothing else.</summary>
public enum DashboardPreset
{
    /// <summary>What is broken now, first. Admins and technicians.</summary>
    Operations = 1,

    /// <summary>Where the service stands, first. Managers — the WP's "executive default".</summary>
    Executive = 2,
}

/// <summary>
/// The whole screen in one read: which views this person has, the layout to draw, and every widget.
/// </summary>
/// <param name="Widgets">
/// Every registered widget, loaded whether or not the current view places it. That is a deliberate cost:
/// swapping a card through its title menu, or adding one, then needs no second round trip — and the total
/// is the same handful of reads the layout used to make anyway, because until views existed every widget
/// was placed on every dashboard.
/// </param>
public sealed record DashboardResponse(
    DashboardLayoutResponse Layout,
    IReadOnlyList<DashboardViewSummary> Views,
    IReadOnlyList<DashboardWidgetResponse> Widgets);

/// <summary>What a write to a view asks for, once the edge has validated it.</summary>
/// <param name="Placements">
/// May be <b>empty</b>: a view created as a blank slate places nothing until somebody adds a card. That is
/// the opposite of the rule this endpoint started with, and the reason is that a named view is a thing
/// somebody curates — see <see cref="DashboardDefaults.Compose"/>.
/// </param>
public sealed record SaveDashboardViewRequest(string? Name, IReadOnlyList<DashboardPlacement>? Placements);
