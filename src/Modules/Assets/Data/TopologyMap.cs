namespace Modules.Assets.Data;

/// <summary>
/// A saved manual layout of the topology map: which CIs an operator has dragged into place, and where
/// they put them.
/// <para>
/// A map is a set of <em>pins</em>, not a snapshot of the estate. A CI with no pin is not hidden — it
/// falls back to auto-layout — so a switch racked next month appears on every saved map rather than on
/// none of them, and a map made today does not quietly become a picture of an estate that no longer
/// exists. That is the whole reason the rows are positions rather than a serialised graph.
/// </para>
/// <para>
/// Maps are shared rather than per-user. A topology diagram is how a team agrees the estate is
/// arranged, and a per-person copy would mean an outage briefing where two people are looking at
/// different pictures.
/// </para>
/// </summary>
public sealed class TopologyMap
{
    public Guid Id { get; set; }

    /// <summary>Unique, because the picker lists maps by name and two "Core network" maps are one too many.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    public ICollection<TopologyMapNode> Nodes { get; set; } = [];

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who last rearranged it. Null until somebody other than its author saves over it.</summary>
    public string? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One CI pinned at one position on one map.</summary>
public sealed class TopologyMapNode
{
    public Guid Id { get; set; }

    public Guid TopologyMapId { get; set; }
    public TopologyMap Map { get; set; } = null!;

    public Guid CiId { get; set; }
    public ConfigurationItem? Ci { get; set; }

    /// <summary>
    /// React Flow's own canvas coordinates, stored as they arrive. Deliberately not normalised or
    /// snapped: the canvas is unbounded and the numbers only ever mean anything to the canvas that
    /// produced them, so inventing a coordinate system here would be a second one to keep in step.
    /// </summary>
    public double X { get; set; }

    public double Y { get; set; }
}
