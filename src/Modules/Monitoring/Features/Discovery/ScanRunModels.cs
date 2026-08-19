using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.Discovery;

/// <param name="Note">
/// Why somebody asked, kept for the audit entry rather than stored on the run. A requested scan is a
/// deliberate act against a network and the audit trail is where "why" belongs.
/// </param>
public sealed record RequestScanRunRequest(string? Note = null);

public sealed record ScanRunListRequest(
    Guid? ScanProfileId = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 25);

/// <param name="AddressesProbed">
/// Null until the scanner reports. Zero devices out of a real address count is a clean sweep of an
/// empty range, which is the difference between "nothing is there" and "the ranges never expanded".
/// </param>
public sealed record ScanRunResponse(
    Guid Id,
    Guid ScanProfileId,
    string ScanProfileName,
    string DiscoveryGroup,
    ScanRunStatus Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? DiscoveryName,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? DeadlineAt,
    DateTimeOffset? CompletedAt,
    int? AddressesProbed,
    int? AddressesTotal,
    int? DevicesFound,
    string? LastRespondingAddress,
    DateTimeOffset? ProgressAt,
    string? Error);

public sealed record ScanRunPageResponse(
    IReadOnlyList<ScanRunResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record ScanRunResult(
    MonitoringOutcome Outcome,
    ScanRunResponse? Run = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

/// <summary>
/// What one scanner is being asked to run right now, handed over already marked as its own.
/// <para>
/// Each entry carries the whole profile rather than only its id, so a scanner can run a request for a
/// profile that is not in its scheduled configuration — an on-demand-only profile, or one whose
/// schedule is switched off. The scanner never has to go and look the profile up.
/// </para>
/// </summary>
public sealed record ScanDispatchResponse(
    string DiscoveryGroup,
    IReadOnlyList<ScanDispatchItem> Runs,
    DateTimeOffset GeneratedAt);

public sealed record ScanDispatchItem(
    Guid ScanRunId,
    DateTimeOffset DeadlineAt,
    DiscoveryScanProfileConfig Profile);

/// <param name="Outcome">
/// <c>Succeeded</c> or <c>Failed</c>. The scanner may not report a run as queued, running or timed
/// out: the first two are not outcomes and the third is the platform's own verdict about the scanner.
/// </param>
public sealed record ReportScanRunRequest(
    string Outcome,
    int? AddressesProbed = null,
    int? DevicesFound = null,
    string? Error = null);

/// <summary>
/// A sweep saying how far it has got, posted repeatedly while a run is in flight.
/// <para>
/// <paramref name="LastRespondingAddress"/> is the last address that <em>answered</em>, not the one
/// being probed: the sweep runs hundreds of probes at once, so there is no single current address, and
/// reporting one would be theatre. What is true is how many have completed and which of them answered.
/// </para>
/// </summary>
public sealed record ReportScanProgressRequest(
    int AddressesProbed,
    int? AddressesTotal = null,
    int? DevicesFound = null,
    string? LastRespondingAddress = null);

public sealed record ScanDispatchResult(
    MonitoringOutcome Outcome,
    ScanDispatchResponse? Dispatch = null);

public sealed record ScanReportResult(
    MonitoringOutcome Outcome,
    ScanRunResponse? Run = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

public sealed record DiscoverySettingsResponse(
    bool ScheduledScanningEnabled,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record UpdateDiscoverySettingsRequest(bool ScheduledScanningEnabled);
