namespace Platform.Data;

public sealed class Department
{
    public Guid Id { get; init; }

    /// <summary>Settable because Settings can rename a department; the id is what everything else holds.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public ICollection<DepartmentSite> Sites { get; } = [];
}
