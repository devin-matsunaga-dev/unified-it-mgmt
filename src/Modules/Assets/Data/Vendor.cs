namespace Modules.Assets.Data;

/// <summary>
/// A supplier the estate buys from and holds contracts with. Distinct from the free-text
/// <c>vendor</c>/<c>manufacturer</c> attributes on network and software CIs, which describe who made
/// a thing rather than who the organisation has an agreement with.
/// </summary>
public sealed class Vendor
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Website { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Contract> Contracts { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
