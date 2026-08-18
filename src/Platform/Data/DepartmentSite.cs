namespace Platform.Data;

/// <summary>
/// Which locations a department operates at. Many-to-many on purpose: the seeded estate already has
/// Information Technology at both Head Office and the Primary Data Centre, and a person's own
/// <see cref="UserProfile.SiteId"/> is independent of their department, so a single-site department
/// would be contradicted by the data on the day it shipped.
/// </summary>
public sealed class DepartmentSite
{
    public Guid DepartmentId { get; init; }

    public Department? Department { get; init; }

    public Guid SiteId { get; init; }

    public Site? Site { get; init; }
}
