using System.Security.Claims;

namespace Platform.Dashboards;

/// <summary>
/// One module's contribution to the unified dashboard, over its own schema and its own services (WP-5.5).
/// <para>
/// This is the same shape as WP-5.4's <c>ISearchSource</c> and deliberately <b>not</b> a port. A port in
/// <c>Platform/Integration</c> exists for the case where neither of two modules may reference the other, and
/// it is narrow because one module is reaching into another's records. Here nobody reaches into anybody:
/// each module answers about its own data, and the only thing that sees all five answers is
/// <see cref="IDashboardService"/>, which holds no reference to any module and is handed them by the
/// container.
/// </para>
/// <para>
/// The consequence worth knowing, and the reason the shape was chosen: <b>adding a widget is a
/// registration.</b> A new widget implements this, adds its member to <see cref="DashboardWidgetType"/> and
/// its place to <see cref="DashboardDefaults"/> — and neither the service, nor the endpoint, nor the browser
/// changes. That is why the title and the tones travel from here rather than being spelt in the SPA.
/// </para>
/// </summary>
public interface IDashboardWidget
{
    /// <summary>Which widget this is. Exactly one implementation per member.</summary>
    DashboardWidgetType Type { get; }

    /// <summary>
    /// The card's heading, in the module's own words. On the server rather than in the browser because a
    /// widget the SPA has never heard of still has to render with a name on it — an untitled card is how
    /// "adding a widget is a registration" would quietly stop being true.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Whether this actor may see this widget at all, answered without touching the database so that a
    /// forbidden widget costs nothing.
    /// <para>
    /// The coarse gate and never the whole rule, as with a search source: a widget that <em>is</em> visible
    /// still narrows its own queries to what this actor may see (ARCHITECTURE §6).
    /// </para>
    /// </summary>
    bool IsVisibleTo(ClaimsPrincipal actor);

    /// <summary>What this widget currently says. Called only when it is both placed and visible.</summary>
    Task<DashboardWidgetData> LoadAsync(DashboardWidgetQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// What a widget is asked. Carries the actor, because a widget narrows its own read, and the row cap, so
/// that "the top five" means the same thing on every card.
/// </summary>
public sealed record DashboardWidgetQuery(ClaimsPrincipal Actor, int RowLimit);
