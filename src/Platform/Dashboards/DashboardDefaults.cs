using System.Security.Claims;

using Platform.Actors;

namespace Platform.Dashboards;

/// <summary>
/// The layout somebody opens on before they have saved a view of their own, and the rules that turn a
/// stored view into the one actually drawn. Pure: no database, no clock, no container — which is what makes
/// every rule below assertable without a host.
/// </summary>
public static class DashboardDefaults
{
    /// <summary>
    /// What is broken now, first. An admin or a technician opens on this.
    /// <para>
    /// Three cards across, then two: DESIGN §5's "KPI row" shape, so the numbers an operator scans in one
    /// glance sit on one line and the two list-shaped cards get the room they need underneath.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DashboardPlacement> Operations { get; } =
    [
        new(DashboardWidgetType.NetworkStatus, DashboardWidgetWidth.Third),
        // A donut on the one widget that is purely a split of a whole. The other four read better as
        // cards, and a dashboard where every card is a different shape is a dashboard nobody can scan.
        new(DashboardWidgetType.OpenByPriority, DashboardWidgetWidth.Third, DashboardDisplay.Donut),
        new(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Third),
        new(DashboardWidgetType.RecentRootCauses, DashboardWidgetWidth.TwoThirds),
        new(DashboardWidgetType.LicenseCompliance, DashboardWidgetWidth.Third),
    ];

    /// <summary>
    /// The WP's executive default: where the service stands and what it is costing, before what is on fire.
    /// SLA health and licence compliance lead on the top row; the root-cause list — an operator's working
    /// list rather than a manager's — sits last.
    /// </summary>
    public static IReadOnlyList<DashboardPlacement> Executive { get; } =
    [
        new(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Third),
        new(DashboardWidgetType.OpenByPriority, DashboardWidgetWidth.Third, DashboardDisplay.Donut),
        new(DashboardWidgetType.LicenseCompliance, DashboardWidgetWidth.Third),
        new(DashboardWidgetType.NetworkStatus, DashboardWidgetWidth.Third),
        new(DashboardWidgetType.RecentRootCauses, DashboardWidgetWidth.TwoThirds),
    ];

    /// <summary>
    /// Which default a role opens on. Manager is the only distinction the platform draws between operators,
    /// and it decides a <em>layout</em> and never a permission — WP-5.4's note that "Manager sees the exec
    /// default" is a different question from which rows anybody may read, and the two must not be conflated.
    /// A manager who is also an admin still gets the executive one: it is the more specific claim about what
    /// somebody is there to do.
    /// </summary>
    public static DashboardPreset PresetFor(ClaimsPrincipal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return ActorRoles.IsManager(actor) ? DashboardPreset.Executive : DashboardPreset.Operations;
    }

    public static IReadOnlyList<DashboardPlacement> For(DashboardPreset preset) =>
        preset == DashboardPreset.Executive ? Executive : Operations;

    /// <summary>
    /// The layout actually drawn, from a saved view (or none), the default for the role, and the widgets
    /// this actor can currently see.
    /// <para>
    /// A placement naming a widget this actor cannot see — or one no longer registered — is <b>dropped</b>.
    /// A saved view outlives a release and outlives a change of role, and a hole in the grid is worse than a
    /// shorter grid. A widget named twice keeps its first place; duplicates are refused at the edge, so a
    /// stored one predates that check.
    /// </para>
    /// <para>
    /// <b>Nothing is appended, and that changed when views did.</b> The first cut of this appended every
    /// visible widget a layout did not name, so that a widget added in a later release could not be
    /// invisible to somebody who had already pressed Save. Once a person can keep several named views and
    /// create an empty one, that rule makes a blank slate impossible and quietly re-adds every card anybody
    /// deliberately removed. A view is now exactly what its owner put in it, and a widget nobody has placed
    /// is reached through the card menu instead — which is where the discoverability the old rule bought
    /// now lives.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DashboardPlacement> Compose(
        IReadOnlyList<DashboardPlacement>? saved,
        DashboardPreset preset,
        IReadOnlyCollection<DashboardWidgetType> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);

        var source = saved ?? For(preset);
        var placed = new List<DashboardPlacement>(source.Count);
        var seen = new HashSet<DashboardWidgetType>();

        foreach (var placement in source)
        {
            if (visible.Contains(placement.Type) && seen.Add(placement.Type))
            {
                placed.Add(placement);
            }
        }

        return placed;
    }
}
