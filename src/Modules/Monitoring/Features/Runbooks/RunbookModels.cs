using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.Runbooks;

/// <summary>
/// How a runbook request ended. Its own enum rather than <c>MonitoringOutcome</c> because two of these
/// have no counterpart there and both are the point of the package: <see cref="NotAllowlisted"/> is a
/// 403 and <see cref="RateLimited"/> is a 429, and folding either into "Invalid" would report a refusal
/// to execute as a typo.
/// </summary>
public enum RunbookOutcome
{
    Success,
    NotFound,
    Invalid,
    Duplicate,

    /// <summary>The key is not in <see cref="RunbookCatalog"/>. Nothing can make this succeed.</summary>
    NotAllowlisted,

    /// <summary>Switched off, estate-wide or per runbook.</summary>
    Disabled,

    /// <summary>Bounded out. Will succeed later; nothing about the request is wrong.</summary>
    RateLimited,

    /// <summary>Something already ran this runbook for this alert.</summary>
    AlreadyRequested,

    /// <summary>A registry write that would orphan history — deleting a runbook that has executions.</summary>
    InUse,
}

// ---- registry ----

/// <param name="Key">A key from <see cref="RunbookCatalog"/>. Anything else is a 403, not a 400.</param>
public sealed record CreateRunbookRequest(
    string Key,
    string? Name = null,
    string? Description = null,
    int? TimeoutSeconds = null,
    int? MaxExecutionsPerWindow = null,
    int? RateLimitWindowMinutes = null,
    bool IsEnabled = true);

/// <summary>
/// The key is absent on purpose: it is what the runbook <em>is</em>, and a registration that could be
/// re-pointed at a different allowlisted action would make every execution's history a lie about which
/// bound applied to it.
/// </summary>
public sealed record UpdateRunbookRequest(
    string? Name = null,
    string? Description = null,
    int? TimeoutSeconds = null,
    int? MaxExecutionsPerWindow = null,
    int? RateLimitWindowMinutes = null,
    bool IsEnabled = true);

/// <param name="Parameters">What this runbook takes, straight from the catalogue, so a caller knows what to supply.</param>
public sealed record RunbookParameterResponse(
    string Name,
    string Description,
    bool IsRequired,
    int MaxLength,
    string Example);

public sealed record RunbookResponse(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    int Version,
    int TimeoutSeconds,
    int MaxExecutionsPerWindow,
    int RateLimitWindowMinutes,
    bool IsEnabled,
    bool IsAllowlisted,
    IReadOnlyList<RunbookParameterResponse> Parameters,
    IReadOnlyList<RunbookTriggerResponse> Triggers,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record RunbookResult(
    RunbookOutcome Outcome,
    RunbookResponse? Runbook = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

// ---- triggers ----

public sealed record SaveRunbookTriggerRequest(
    string MetricName,
    string MinimumSeverity,
    Guid? DeviceId = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool IsEnabled = true);

public sealed record RunbookTriggerResponse(
    Guid Id,
    Guid RunbookId,
    string MetricName,
    string MinimumSeverity,
    Guid? DeviceId,
    IReadOnlyDictionary<string, string> Parameters,
    bool IsEnabled,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record RunbookTriggerResult(
    RunbookOutcome Outcome,
    RunbookTriggerResponse? Trigger = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

// ---- executions ----

/// <summary>
/// An operator asking for a runbook by hand. It names a device and parameters, and nothing else —
/// there is no field here through which a command, a script or a host could be supplied.
/// </summary>
public sealed record RunRunbookRequest(
    Guid DeviceId,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record RunbookExecutionResponse(
    Guid Id,
    Guid RunbookId,
    string RunbookKey,
    string RunbookName,
    int RunbookVersion,
    Guid? TriggerId,
    Guid? AlertId,
    Guid DeviceId,
    Guid CiId,
    string? RuleId,
    IReadOnlyDictionary<string, string> Parameters,
    string Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? PollerName,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? DeadlineAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    string? Output,
    string? Error);

public sealed record RunbookExecutionPageResponse(
    IReadOnlyList<RunbookExecutionResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record RunbookExecutionListRequest(
    Guid? RunbookId = null,
    Guid? DeviceId = null,
    RunbookExecutionStatus? Status = null,
    int Page = 1,
    int PageSize = 25);

public sealed record RunbookExecutionResult(
    RunbookOutcome Outcome,
    RunbookExecutionResponse? Execution = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

// ---- the poller channel ----

/// <summary>
/// One execution as an agent is told about it. Note what is not here: no credential, no address to
/// authenticate against, no command. A runbook is given the device it concerns and the parameters an
/// operator wrote, and the agent turns the key into an action itself.
/// </summary>
public sealed record RunbookDispatchItem(
    Guid ExecutionId,
    string RunbookKey,
    int RunbookVersion,
    Guid DeviceId,
    Guid CiId,
    string? CiName,
    string Address,
    IReadOnlyDictionary<string, string> Parameters,
    int TimeoutSeconds,
    DateTimeOffset DeadlineAt);

public sealed record RunbookDispatchResponse(
    string PollerName,
    string PollerGroup,
    IReadOnlyList<RunbookDispatchItem> Executions,
    DateTimeOffset GeneratedAt);

public sealed record RunbookDispatchResult(
    MonitoringOutcome Outcome,
    RunbookDispatchResponse? Dispatch = null);

/// <param name="Outcome"><c>Succeeded</c>, <c>Failed</c> or <c>TimedOut</c>; anything else is a 400.</param>
public sealed record ReportRunbookResultRequest(
    string Outcome,
    int? ExitCode = null,
    string? Output = null,
    string? Error = null);

public sealed record RunbookReportResult(
    MonitoringOutcome Outcome,
    RunbookExecutionResponse? Execution = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
