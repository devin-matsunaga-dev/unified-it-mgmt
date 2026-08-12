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

/// <summary>
/// The per-check overrides of WP-3.5's alert tuning. Every member is optional and a null one means
/// "use the platform default from <c>Monitoring:Alerting</c>" — a check never stores a copy of the
/// defaults, so raising the platform's sustain count raises it for every check that never asked for
/// something else.
/// </summary>
public sealed record AlertTuningRequest(
    int? SustainedCycles = null,
    int? RecoveryCycles = null,
    double? HysteresisPercent = null,
    int? FlapThreshold = null,
    int? FlapWindowSeconds = null);

/// <param name="CredentialId">
/// The vault credential this check authenticates with (WP-3.11), or null. Like the alert tuning block
/// it is a complete statement: omitting it on an update detaches the credential rather than keeping
/// the previous one, so "this check no longer authenticates" is expressible.
/// </param>
public sealed record CreateCheckRequest(
    CheckType Type,
    string Name,
    int IntervalSeconds,
    int TimeoutSeconds,
    double? WarningThreshold = null,
    double? CriticalThreshold = null,
    ThresholdComparison Comparison = ThresholdComparison.GreaterThan,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool IsEnabled = true,
    AlertTuningRequest? AlertTuning = null,
    Guid? CredentialId = null);

public sealed record UpdateCheckRequest(
    string Name,
    int IntervalSeconds,
    int TimeoutSeconds,
    double? WarningThreshold = null,
    double? CriticalThreshold = null,
    ThresholdComparison Comparison = ThresholdComparison.GreaterThan,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool IsEnabled = true,
    AlertTuningRequest? AlertTuning = null,
    Guid? CredentialId = null);

/// <param name="CredentialName">
/// Read live from the vault so a check names the credential an operator recognises. Null when the
/// check authenticates to nothing — and also when the credential has gone, which is the state the
/// vault's delete guard exists to prevent.
/// </param>
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
    AlertTuningRequest AlertTuning,
    bool IsEnabled,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    Guid? CredentialId = null,
    string? CredentialName = null);

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
