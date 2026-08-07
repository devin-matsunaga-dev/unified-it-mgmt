namespace Modules.Assets.Data;

/// <summary>Where a CI sits in its life. Ordered is a CI that has been bought but not yet received.</summary>
public enum CiLifecycleState
{
    Ordered = 1,
    InStock = 2,
    Deployed = 3,
    InRepair = 4,
    Retired = 5,
    Disposed = 6,
}

/// <summary>
/// One legal move in the lifecycle graph. Persisted rather than compiled in, so the guard is data an
/// operator can inspect — the same shape WP-1.2 gave ticket statuses.
/// </summary>
public sealed class CiLifecycleTransition
{
    public CiLifecycleState FromState { get; set; }
    public CiLifecycleState ToState { get; set; }
}

public sealed class CiLifecycleHistory
{
    public Guid Id { get; set; }
    public Guid CiId { get; set; }
    public ConfigurationItem Ci { get; set; } = null!;
    public CiLifecycleState FromState { get; set; }
    public CiLifecycleState ToState { get; set; }
    public string? Note { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>What happened to a CI's ownership. Checking out is assigning a person, checking in is clearing one.</summary>
public enum CiAssignmentAction
{
    CheckOut = 1,
    CheckIn = 2,
    Transfer = 3,
    Relocate = 4,
}

/// <summary>The check-in/check-out log: one append-only row per ownership change.</summary>
public sealed class CiAssignmentEntry
{
    public Guid Id { get; set; }
    public Guid CiId { get; set; }
    public ConfigurationItem Ci { get; set; } = null!;
    public CiAssignmentAction Action { get; set; }
    public Guid? FromOwnerUserId { get; set; }
    public string? FromOwnerName { get; set; }
    public Guid? ToOwnerUserId { get; set; }
    public string? ToOwnerName { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public Guid? SiteId { get; set; }
    public string? SiteName { get; set; }
    public string? Note { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
