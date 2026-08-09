using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.MaintenanceWindows;

public sealed record CreateMaintenanceWindowRequest(
    string Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Description = null,
    bool AppliesToAllDevices = false,
    IReadOnlyList<Guid>? DeviceIds = null,
    bool IsActive = true);

public sealed record UpdateMaintenanceWindowRequest(
    string Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Description = null,
    bool AppliesToAllDevices = false,
    IReadOnlyList<Guid>? DeviceIds = null,
    bool IsActive = true);

public sealed record MaintenanceWindowListRequest(
    string? Search = null,
    Guid? DeviceId = null,
    bool? IsActive = null,
    MaintenanceWindowStatus? Status = null,
    int Page = 1,
    int PageSize = 25);

public sealed record MaintenanceWindowResponse(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool AppliesToAllDevices,
    IReadOnlyList<Guid> DeviceIds,
    bool IsActive,
    MaintenanceWindowStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceWindowPageResponse(
    IReadOnlyList<MaintenanceWindowResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// Where a window sits relative to now, computed at read time rather than stored — a stored status is
/// wrong every minute until something rewrites it, the same reasoning as WP-2.6's contract status.
/// </summary>
public enum MaintenanceWindowStatus
{
    Scheduled,
    InProgress,
    Ended,
}

public sealed record MaintenanceWindowResult(
    MonitoringOutcome Outcome,
    MaintenanceWindowResponse? Window = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
