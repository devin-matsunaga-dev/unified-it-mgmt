using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.PollerConfig;

public sealed record RegisterPollerRequest(
    string Name,
    string? PollerGroup = null,
    string? AgentVersion = null);

public sealed record PollerResponse(
    Guid Id,
    string Name,
    string PollerGroup,
    string? AgentVersion,
    long LastConfigVersion,
    DateTimeOffset? LastConfigFetchedAt,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastRegisteredAt,
    bool IsEnabled,
    long CurrentConfigVersion);

public sealed record PollerListResponse(IReadOnlyList<PollerResponse> Items, long CurrentConfigVersion);

/// <summary>
/// What a poller should be doing, as of <see cref="ConfigVersion"/>. A full snapshot lists every
/// device in the poller's group; a delta lists only the devices that changed since the version the
/// poller asked from, plus the ids it should forget. Maintenance windows are always sent whole —
/// there are few of them and a poller that mutes the wrong device is worse than a poller that
/// re-reads a short list.
/// </summary>
public sealed record PollerConfigResponse(
    string PollerName,
    string PollerGroup,
    long ConfigVersion,
    bool IsFullSnapshot,
    IReadOnlyList<PollerDeviceConfig> Devices,
    IReadOnlyList<Guid> RemovedDeviceIds,
    IReadOnlyList<PollerMaintenanceWindowConfig> MaintenanceWindows,
    DateTimeOffset GeneratedAt);

/// <summary>
/// A device is sent whole or not at all: a check edit re-sends its device. The poller's unit of work
/// is a device, so a partial device would have to be merged by every poller implementation.
/// </summary>
public sealed record PollerDeviceConfig(
    Guid DeviceId,
    Guid CiId,
    string? CiName,
    string Address,
    IReadOnlyList<PollerCheckConfig> Checks);

public sealed record PollerCheckConfig(
    Guid CheckId,
    CheckType Type,
    string Name,
    int IntervalSeconds,
    int TimeoutSeconds,
    double? WarningThreshold,
    double? CriticalThreshold,
    ThresholdComparison Comparison,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record PollerMaintenanceWindowConfig(
    Guid Id,
    string Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool AppliesToAllDevices,
    IReadOnlyList<Guid> DeviceIds);

public sealed record PollerResult(
    MonitoringOutcome Outcome,
    PollerResponse? Poller = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

public sealed record PollerConfigResult(
    MonitoringOutcome Outcome,
    PollerConfigResponse? Config = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
