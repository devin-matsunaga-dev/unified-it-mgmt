namespace Platform.Data;

/// <summary>
/// One named arrangement of the unified dashboard, owned by one person (WP-5.5). Several per owner, exactly
/// one of them active.
/// <para>
/// <b>Per person and not per role, deliberately.</b> The WP's phrase is "save per role", and read literally
/// that would mean one manager dragging a card changes every other manager's screen — a shared document
/// with no owner, no history and no way to tell who last moved anything. What the role actually decides is
/// which default somebody starts from (<see cref="Platform.Dashboards.DashboardDefaults.PresetFor"/>).
/// </para>
/// <para>
/// The placements are one jsonb column rather than a child table, following WP-1.9's saved ticket views:
/// nothing queries into a layout, it is read and written whole, and a row-per-widget table would be a join
/// and an ordering column for data that is only ever handed back the way it went in.
/// </para>
/// </summary>
public sealed class DashboardView
{
    public Guid Id { get; set; }

    /// <summary>The owner's immutable identity claim — never a display name.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>What the owner calls this view. Unique per owner, because the tabs are read by name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// An ordered array of <c>{ "type": …, "width": … }</c>, validated at the edge before it lands here.
    /// <b>May be empty</b>: a view created as a blank slate has no cards until somebody adds one.
    /// </summary>
    public string PlacementsJson { get; set; } = "[]";

    /// <summary>
    /// Whether this is the view the owner is currently looking at. At most one per owner is true, which is
    /// held by the service rather than by a constraint — swapping which row is active is two writes in one
    /// transaction, and a database that refused the intermediate state would refuse the swap.
    /// </summary>
    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
