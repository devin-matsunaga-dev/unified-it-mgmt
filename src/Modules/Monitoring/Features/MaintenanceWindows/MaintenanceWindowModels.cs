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

/// <param name="From">
/// Lower bound, inclusive. Both bounds are optional and a window is in range when it overlaps the range
/// at all, matching the change calendar's own rule: a window straddling the first of the month belongs to
/// both months rather than to neither.
/// </param>
public sealed record MaintenanceWindowListRequest(
    string? Search = null,
    Guid? DeviceId = null,
    bool? IsActive = null,
    MaintenanceWindowStatus? Status = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 25);

/// <param name="ChangeRequestId">
/// The approved change that opened this window, or null for one an operator created directly (WP-5.8).
/// It is what lets the change calendar draw a window next to the change it came from without either
/// module reading the other's schema.
/// </param>
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
    DateTimeOffset UpdatedAt,
    Guid? ChangeRequestId = null);

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
