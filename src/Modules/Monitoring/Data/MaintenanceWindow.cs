namespace Modules.Monitoring.Data;

/// <summary>
/// A period during which a set of devices is expected to be disturbed. WP-3.1 models and serves the
/// windows; muting an alert inside one is WP-3.5's job, so nothing here reads them yet apart from the
/// poller config document.
/// </summary>
public sealed class MaintenanceWindow
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    /// <summary>
    /// An estate-wide window (a power test, a WAN cutover) covers devices that do not exist yet when
    /// it is scheduled, so it is a flag rather than a list built at creation time.
    /// </summary>
    public bool AppliesToAllDevices { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The Assets change request whose approval opened this window, or null for one an operator created
    /// directly (WP-5.8).
    /// <para>
    /// An id and nothing else — no name, no schedule copied across. Monitoring may not query
    /// <c>assets.change_requests</c> and no foreign key can span the two schemas, so this is a trace back
    /// to a record rather than a relationship the database enforces. It carries a filtered unique index,
    /// which is what makes the sync idempotent: one window per change, however many times the approval is
    /// delivered.
    /// </para>
    /// </summary>
    public Guid? ChangeRequestId { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MaintenanceWindowDevice> Devices { get; set; } = [];
}

/// <summary>Which devices a scoped window covers. Empty when the window applies to all of them.</summary>
public sealed class MaintenanceWindowDevice
{
    public Guid MaintenanceWindowId { get; set; }

    public MaintenanceWindow MaintenanceWindow { get; set; } = null!;

    public Guid DeviceId { get; set; }

    public MonitoredDevice Device { get; set; } = null!;
}
