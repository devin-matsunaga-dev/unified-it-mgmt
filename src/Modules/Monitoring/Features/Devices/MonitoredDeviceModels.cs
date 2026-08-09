using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Devices;

public sealed record CreateMonitoredDeviceRequest(
    Guid CiId,
    string Address,
    string? PollerGroup = null,
    bool IsEnabled = true,
    string? Notes = null);

/// <summary>
/// The CI a device is cannot be changed: a device is the monitoring of one CI, and repointing it
/// would silently re-attribute every metric and alert already recorded against it.
/// </summary>
public sealed record UpdateMonitoredDeviceRequest(
    string Address,
    string? PollerGroup = null,
    bool IsEnabled = true,
    string? Notes = null);

public sealed record MonitoredDeviceListRequest(
    string? Search = null,
    Guid? CiId = null,
    string? PollerGroup = null,
    bool? IsEnabled = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>
/// A device plus the CI context needed to read it. The CI fields are read live through the port and
/// are null only if the CI has gone — which nothing currently prevents, because no delete guard spans
/// Assets and Monitoring yet.
/// </summary>
public sealed record MonitoredDeviceResponse(
    Guid Id,
    Guid CiId,
    string? CiName,
    string? CiType,
    string? CiLifecycleState,
    string? SiteName,
    string Address,
    string PollerGroup,
    bool IsEnabled,
    string? Notes,
    int CheckCount,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record MonitoredDevicePageResponse(
    IReadOnlyList<MonitoredDeviceResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record CreateCheckRequest(
    CheckType Type,
    string Name,
    int IntervalSeconds,
    int TimeoutSeconds,
    double? WarningThreshold = null,
    double? CriticalThreshold = null,
    ThresholdComparison Comparison = ThresholdComparison.GreaterThan,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool IsEnabled = true);

public sealed record UpdateCheckRequest(
    string Name,
    int IntervalSeconds,
    int TimeoutSeconds,
    double? WarningThreshold = null,
    double? CriticalThreshold = null,
    ThresholdComparison Comparison = ThresholdComparison.GreaterThan,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool IsEnabled = true);

public sealed record CheckResponse(
    Guid Id,
    Guid DeviceId,
    CheckType Type,
    string Name,
    int IntervalSeconds,
    int TimeoutSeconds,
    double? WarningThreshold,
    double? CriticalThreshold,
    ThresholdComparison Comparison,
    IReadOnlyDictionary<string, string> Parameters,
    bool IsEnabled,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public enum MonitoringOutcome
{
    Success,
    NotFound,
    Invalid,
    Duplicate,
}

public sealed record MonitoredDeviceResult(
    MonitoringOutcome Outcome,
    MonitoredDeviceResponse? Device = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

public sealed record CheckResult(
    MonitoringOutcome Outcome,
    CheckResponse? Check = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
