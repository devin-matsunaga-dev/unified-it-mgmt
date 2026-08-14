using Modules.Assets.Data;

namespace Modules.Assets.Features.Drift;

/// <summary>
/// What kind of disagreement a finding is. The three the WP text names, and they are all about one
/// field of one CI: the CMDB's recorded value against what the last scan observed.
/// </summary>
public enum DriftFindingKind
{
    /// <summary>
    /// A scan observed a value for a field the CMDB leaves blank. Nothing is wrong with the estate —
    /// somebody has an answer to type in.
    /// </summary>
    New = 1,

    /// <summary>
    /// The CMDB records something the network no longer confirms: a field a device that <em>did</em>
    /// answer SNMP left empty, or the CI itself, which no scan has reported for longer than the report's
    /// staleness threshold.
    /// </summary>
    Missing = 2,

    /// <summary>Both sides have a value and they disagree. The finding worth acting on.</summary>
    Changed = 3,
}

/// <summary>
/// The fields the report compares, as the API and the browser name them. Deliberately short: a pair is
/// only here when the CMDB has a home for the value <em>and</em> a scan has a source for it, because a
/// comparison missing either half produces a finding nobody can act on.
/// </summary>
public static class DriftFields
{
    /// <summary>The CI's site name against the device's own <c>sysLocation</c>.</summary>
    public const string Location = "location";

    /// <summary>A server or virtual CI's hostname against what the device calls itself.</summary>
    public const string Hostname = "hostname";

    /// <summary>A network CI's management IP against the address a scan found it on.</summary>
    public const string ManagementIp = "managementIp";

    /// <summary>
    /// Not a field of the CI at all: the finding that no scan has seen this CI lately. It is filed as a
    /// field so one list can carry it, and it is the only finding whose recorded side is the CI itself.
    /// </summary>
    public const string LastSeen = "lastSeen";

    public static readonly IReadOnlyList<string> All = [Location, Hostname, ManagementIp, LastSeen];

    public static string LabelOf(string field) => field switch
    {
        Location => "Location",
        Hostname => "Hostname",
        ManagementIp => "Management IP",
        LastSeen => "Last seen by discovery",
        _ => field,
    };
}

/// <summary>
/// One CI as the comparator sees it: the recorded half. Built by the service from the CI row so that
/// <see cref="DriftAnalyzer"/> itself needs no database, no clock and no configuration.
/// </summary>
/// <param name="RecordedHostname">
/// Null for a type that records no hostname — hardware, network, software and logical CIs — which is
/// how the comparator knows to skip the field rather than report it missing on every switch.
/// </param>
/// <param name="RecordedManagementIp">Null for every type but <see cref="CiType.NetworkDevice"/>, for the same reason.</param>
public sealed record DriftSubject(
    Guid CiId,
    string Name,
    CiType Type,
    Guid? SiteId,
    string? SiteName,
    string? RecordedHostname,
    string? RecordedManagementIp,
    DriftObservation Observation);

/// <summary>
/// The observed half: <c>assets.ci_discovery_facts</c>, which WP-4.2 writes on every match and which
/// nothing else in the platform touches. A CI with no row here is not compared at all — no scan has
/// ever reported it, and silence is not drift.
/// </summary>
/// <param name="AnsweredSnmp">
/// Whether the device answered the SNMP system group at all. It gates every <see cref="DriftFindingKind.Missing"/>
/// field finding: a device that only answered a ping has said nothing about its location, while one
/// that answered SNMP and left <c>sysLocation</c> empty has.
/// </param>
public sealed record DriftObservation(
    string Address,
    string? Hostname,
    string? SysName,
    string? SysLocation,
    string? SysDescription,
    bool AnsweredSnmp,
    DateTimeOffset LastSeenAt);

/// <summary>One disagreement, in the form the table renders.</summary>
/// <param name="RecordedValue">What the CMDB says. Null when the finding is that it says nothing.</param>
/// <param name="ObservedValue">What the last scan saw. Null when the finding is that it saw nothing.</param>
public sealed record DriftFinding(
    string Field,
    DriftFindingKind Kind,
    string? RecordedValue,
    string? ObservedValue);

/// <summary>Every finding against one CI, with enough of the CI to render a row without a second read.</summary>
public sealed record CiDriftResponse(
    Guid CiId,
    string Name,
    CiType Type,
    string? SiteName,
    string Address,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<DriftFindingResponse> Findings);

public sealed record DriftFindingResponse(
    string Field,
    string Label,
    DriftFindingKind Kind,
    string? RecordedValue,
    string? ObservedValue);

/// <summary>
/// A cable a scan saw that no relationship records. WP-4.3 computes these for the map and writes none
/// of them down — deliberately, so that this report has something to find. It is the third shape of
/// "new": not a field and not a CI, but an edge.
/// </summary>
public sealed record UnrecordedLinkResponse(
    Guid SourceCiId,
    string SourceCiName,
    string? SourcePort,
    Guid TargetCiId,
    string TargetCiName,
    string? TargetPort,
    IReadOnlyList<string> Protocols,
    bool ConfirmedByBothEnds);

/// <param name="CisObserved">How many CIs a scan has ever reported. The denominator of everything below.</param>
/// <param name="UnmatchedDiscoveries">
/// Discoveries sitting in WP-4.2's review queue: something on the network that answers to no CI at all.
/// Counted rather than listed, because the queue is where they are dealt with and it is one click away.
/// </param>
/// <param name="StaleAfterDays">The threshold that decided which CIs count as unseen, echoed so the number can be read.</param>
public sealed record DriftSummaryResponse(
    int CisObserved,
    int CisWithDrift,
    int Changed,
    int New,
    int Missing,
    int UnrecordedLinks,
    int UnmatchedDiscoveries,
    int StaleAfterDays,
    DateTimeOffset GeneratedAt);

public sealed record DriftReportResponse(
    DriftSummaryResponse Summary,
    IReadOnlyList<CiDriftResponse> Items,
    IReadOnlyList<UnrecordedLinkResponse> UnrecordedLinks,
    int Total,
    int Page,
    int PageSize);

/// <param name="Kind">Narrows the report to one kind of finding; a CI keeps only the findings that match.</param>
/// <param name="Field">Narrows it to one field, the same way.</param>
/// <param name="StaleAfterDays">
/// Overrides the configured threshold for this read. An operator arguing about whether a device is
/// really gone wants to move it, and moving it must not need a deployment.
/// </param>
public sealed record DriftReportRequest(
    DriftFindingKind? Kind = null,
    string? Field = null,
    Guid? SiteId = null,
    int? StaleAfterDays = null,
    int Page = 1,
    int PageSize = 25);
