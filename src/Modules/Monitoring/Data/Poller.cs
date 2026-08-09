namespace Modules.Monitoring.Data;

/// <summary>
/// A polling service that has introduced itself to the platform. Registration is an upsert keyed on
/// the poller's name, so a poller that restarts — or is redeployed with a new container id — is the
/// same poller rather than a second one. WP-3.2 gives it credentials and a heartbeat; here it exists
/// only so a config fetch has something to be scoped by.
/// </summary>
public sealed class Poller
{
    public Guid Id { get; set; }

    /// <summary>The poller's own stable name, and the natural key registration matches on.</summary>
    public required string Name { get; set; }

    /// <summary>The device group this poller is responsible for.</summary>
    public required string PollerGroup { get; set; }

    /// <summary>The agent build that registered, for the day a config shape changes.</summary>
    public string? AgentVersion { get; set; }

    /// <summary>The config version handed to it by its last successful fetch; 0 before the first.</summary>
    public long LastConfigVersion { get; set; }

    public DateTimeOffset? LastConfigFetchedAt { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    public DateTimeOffset LastRegisteredAt { get; set; }

    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// One row per configuration change, carrying the version it happened at. This is what makes a delta
/// honest: a computed diff between two documents cannot see a device that was created and deleted
/// between two fetches, and cannot tell "removed" from "never existed".
/// </summary>
public sealed class MonitoringConfigChange
{
    /// <summary>
    /// Monotonic, allocated under a transaction-scoped advisory lock so versions commit in the order
    /// they are issued. Without the lock a poller could read version 6 while 5 was still uncommitted
    /// and never see change 5 again.
    /// </summary>
    public long Version { get; set; }

    public MonitoringConfigEntity EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary>
    /// The device the change belongs to — its own id for a device change, the parent for a check.
    /// Null for a maintenance window, which is not scoped to one device.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// The group affected, recorded at change time. A device that moves between groups writes a
    /// change against the group it left as well, so the old group's poller learns to drop it.
    /// </summary>
    public string? PollerGroup { get; set; }

    public MonitoringConfigChangeKind Kind { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public enum MonitoringConfigEntity
{
    Device,
    Check,
    MaintenanceWindow,
}

public enum MonitoringConfigChangeKind
{
    Upserted,
    Removed,
}
