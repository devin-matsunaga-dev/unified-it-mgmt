namespace Platform.Data;

/// <summary>A physical location. "Location" in the asset vocabulary; stored as a site throughout.</summary>
public sealed class Site
{
    public Guid Id { get; init; }

    public required string Code { get; set; }

    public required string Name { get; set; }

    public ICollection<DepartmentSite> Departments { get; } = [];
}
