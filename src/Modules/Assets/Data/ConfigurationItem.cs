namespace Modules.Assets.Data;

/// <summary>
/// The CMDB backbone record. Stored table-per-hierarchy in <c>assets.cis</c>: every derived type
/// shares the identity columns below and adds its own attribute columns, which TPH makes physically
/// nullable — required-ness per type is enforced by <see cref="Features.Cis.CiTypeSchema"/>.
/// </summary>
public abstract class ConfigurationItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AssetTag { get; set; }
    public string? SerialNumber { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public CiLifecycleState LifecycleState { get; set; } = CiLifecycleState.InStock;

    // Ownership. The ids address platform rows this module may not join to, so each carries a name
    // snapshot for rendering; see CiAssignmentEntry for the same reasoning.
    public Guid? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public Guid? SiteId { get; set; }
    public string? SiteName { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }

    // Coverage. A CI is bought once and covered by at most one support agreement; the contract is
    // read live rather than snapshotted because, unlike a person, it cannot leave the module.
    public DateOnly? PurchaseDate { get; set; }
    public DateOnly? WarrantyExpiresAt { get; set; }
    public Guid? ContractId { get; set; }
    public Contract? Contract { get; set; }

    public ICollection<CiCustomFieldValue> CustomFieldValues { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The discriminator value EF stores in <c>ci_type</c>.</summary>
    public abstract CiType Type { get; }
}

public sealed class HardwareCi : ConfigurationItem
{
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public override CiType Type => CiType.Hardware;
}

public sealed class ServerCi : ConfigurationItem
{
    public string Hostname { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public int CpuCores { get; set; }
    public int RamGb { get; set; }
    public override CiType Type => CiType.Server;
}

public sealed class NetworkDeviceCi : ConfigurationItem
{
    public string ManagementIp { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public int PortCount { get; set; }
    public override CiType Type => CiType.NetworkDevice;
}

public sealed class SoftwareCi : ConfigurationItem
{
    public string Vendor { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public override CiType Type => CiType.Software;
}

public sealed class VirtualCi : ConfigurationItem
{
    public string Hostname { get; set; } = string.Empty;
    public string Hypervisor { get; set; } = string.Empty;
    public int VcpuCores { get; set; }
    public int RamGb { get; set; }
    public override CiType Type => CiType.Virtual;
}

public sealed class LogicalCi : ConfigurationItem
{
    public string Purpose { get; set; } = string.Empty;
    public string ServiceTier { get; set; } = string.Empty;
    public override CiType Type => CiType.Logical;
}

public enum CiType
{
    Hardware = 1,
    Server = 2,
    NetworkDevice = 3,
    Software = 4,
    Virtual = 5,
    Logical = 6,
}
