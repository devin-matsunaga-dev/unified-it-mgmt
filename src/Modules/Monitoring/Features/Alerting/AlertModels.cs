using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Alerting;

/// <param name="Status">
/// Which half of the history to read. Defaults to <see cref="AlertStatus.Open"/>, because an alert
/// board is a list of what is wrong now — the cleared ones are history and have to be asked for.
/// </param>
/// <param name="Acknowledged">
/// Null for both. True is "somebody is on it", false is the queue nobody has picked up yet, which is
/// the filter an operator actually works from.
/// </param>
public sealed record AlertListRequest(
    AlertStatus? Status = AlertStatus.Open,
    AlertSeverity? Severity = null,
    Guid? DeviceId = null,
    Guid? CiId = null,
    bool? Acknowledged = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>
/// One row of the alert board. The CMDB fields (<paramref name="CiName"/> onwards) are read live
/// through the WP-3.7 enrichment on every request and none of them is stored on the alert row — the
/// same rule WP-2.4 set for ticket links. Open related tickets are deliberately absent: they cost a
/// query per row, so they only appear on <see cref="AlertDetailResponse"/>.
/// </summary>
/// <param name="IsFlapping">
/// True while the rule is changing state faster than the flap policy allows. Shown rather than
/// hidden, because a flapping rule publishes nothing — the board is the only place the fact is
/// visible without reading Redis.
/// </param>
/// <param name="Suppression">
/// Why this alert told nobody, if it did not. Expect to see an alert at severity <c>Ok</c> that is
/// still <c>Open</c> here: a muted rule that recovers keeps its row until suppression lifts and the
/// next reading reconciles it (WP-3.5).
/// </param>
public sealed record AlertResponse(
    Guid Id,
    Guid DeviceId,
    Guid CiId,
    Guid CheckId,
    string RuleId,
    string MetricName,
    AlertSeverity Severity,
    AlertStatus Status,
    string Summary,
    double? LastValue,
    double? Threshold,
    int ConsecutiveBreaches,
    bool IsFlapping,
    AlertSuppression Suppression,
    DateTimeOffset RaisedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset? ClearedAt,
    string PollerName,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedBy,
    string? AcknowledgedByName,
    string? DeviceAddress,
    string? CheckName,
    bool CiFound,
    string? CiName,
    string? CiType,
    string? AssetTag,
    string? LifecycleState,
    string? OwnerName,
    string? SiteName,
    string? DepartmentName,
    DateOnly? WarrantyExpiresAt,
    string? WarrantyStatus,
    int? WarrantyDaysRemaining,
    string? ContractName);

/// <summary>
/// One alert with the whole WP-3.7 context, including what is already being worked on for its CI.
/// This is the "shown on alert board" half that WP-3.7 recorded as belonging here.
/// </summary>
public sealed record AlertDetailResponse(
    AlertResponse Alert,
    IReadOnlyList<Platform.Integration.LinkedTicketSummary> OpenTickets);

public sealed record AlertPageResponse(
    IReadOnlyList<AlertResponse> Items,
    int Total,
    int Page,
    int PageSize,
    AlertCounts Counts);

/// <summary>
/// How many open alerts there are by severity, for the board's KPI row. Counted over every open alert
/// rather than over the page, so turning a page does not change the headline number.
/// </summary>
public sealed record AlertCounts(int Open, int Critical, int Warning, int Unacknowledged);

public enum AlertActionOutcome
{
    Success,
    NotFound,

    /// <summary>Already acknowledged, or not open. Both are 409s that name what is in the way.</summary>
    Conflict,
}

public sealed record AlertActionResult(
    AlertActionOutcome Outcome,
    AlertResponse? Alert = null,
    string? Error = null);
