namespace Modules.Assets.Data;

/// <summary>
/// How one CI relates to another. Every type reads source-first — "this VM <c>RunsOn</c> that host" —
/// so the source is always the dependant and the target is always the dependency.
/// </summary>
public enum CiRelationshipType
{
    RunsOn = 1,
    ConnectsTo = 2,
    DependsOn = 3,
    HostedOn = 4,
}

/// <summary>
/// One directed edge of the dependency graph. The direction is the whole meaning of the row: the
/// source needs the target, so a failing target takes the source down with it. Traversal is a
/// recursive CTE over this table — see <see cref="Features.Relationships.CiGraphQuery"/>.
/// </summary>
public sealed class CiRelationship
{
    public Guid Id { get; set; }

    /// <summary>The dependant — the CI that stops working when the target does.</summary>
    public Guid SourceCiId { get; set; }
    public ConfigurationItem SourceCi { get; set; } = null!;

    /// <summary>The dependency — what the source needs in order to work.</summary>
    public Guid TargetCiId { get; set; }
    public ConfigurationItem TargetCi { get; set; } = null!;

    public CiRelationshipType Type { get; set; }
    public string? Description { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One row of a recursive-CTE traversal: a CI the walk reached, and the fewest hops it took to get
/// there. Keyless — it is the shape of a query result, never a table.
/// </summary>
public sealed class CiGraphHop
{
    public Guid CiId { get; set; }
    public int Depth { get; set; }
}
