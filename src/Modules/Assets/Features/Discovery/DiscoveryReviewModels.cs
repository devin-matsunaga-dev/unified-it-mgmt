using Modules.Assets.Data;

namespace Modules.Assets.Features.Discovery;

public sealed record DiscoveredDeviceListRequest(
    DiscoveredDeviceStatus? Status = DiscoveredDeviceStatus.Pending,
    string? Search = null,
    Guid? ScanProfileId = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>
/// What a scan learned about one device, as the review card renders it.
/// </summary>
/// <param name="MatchRule">
/// Why the platform placed it where it did — or <c>Ambiguous</c>, in which case
/// <paramref name="Contenders"/> names the CIs that tied and the card asks a human to choose.
/// </param>
/// <param name="SuggestedType">
/// The CI type an approval would default to, inferred from what answered. A suggestion the approver
/// overrides freely; nothing is created from it without them pressing the button.
/// </param>
/// <param name="HostnameSource">
/// Which protocol named it: <c>dns</c>, <c>mdns</c> or <c>netbios</c>. Null when nothing did.
/// </param>
public sealed record DiscoveredDeviceResponse(
    Guid Id,
    string IdentityKey,
    string Address,
    string? Hostname,
    string? HostnameSource,
    bool RespondedToPing,
    IReadOnlyList<int> OpenPorts,
    DiscoveredSnmpResponse? Snmp,
    IReadOnlyList<DiscoveredNeighbourResponse> Neighbours,
    string DiscoveryName,
    Guid ScanProfileId,
    string ScanProfileName,
    DiscoveredDeviceStatus Status,
    Guid? CiId,
    string? CiName,
    DiscoveryMatchRule MatchRule,
    IReadOnlyList<DiscoveryContenderResponse> Contenders,
    CiType SuggestedType,
    string SuggestedName,
    IReadOnlyDictionary<string, string> SuggestedAttributes,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int SightingCount,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

public sealed record DiscoveredSnmpResponse(
    string? SysName,
    string? SysDescription,
    string? SysObjectId,
    string? SysLocation,
    string? SysContact,
    double? UptimeSeconds);

public sealed record DiscoveredNeighbourResponse(
    string Protocol,
    string? LocalPort,
    string? RemoteSystemName,
    string? RemotePort,
    string? RemoteAddress);

/// <summary>One of the CIs an ambiguous match tied between, named so a human can tell them apart.</summary>
public sealed record DiscoveryContenderResponse(Guid CiId, string Name, CiType Type);

public sealed record DiscoveredDevicePageResponse(
    IReadOnlyList<DiscoveredDeviceResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// What the approver decided. Everything is optional except the type, because the card has already
/// filled it in from the scan — this is a confirmation with edits, not a form typed from nothing.
/// </summary>
/// <param name="CiId">
/// Set to attach the discovery to a CI that already exists — the way an ambiguous match is settled —
/// instead of creating one. Mutually exclusive with the creation fields.
/// </param>
/// <param name="Attributes">
/// The chosen type's attributes. Discovery fills in only what it genuinely observed — a network CI's
/// management IP, a server's hostname — and the approver supplies the rest, because <c>CiTypeSchema</c>
/// requires vendor and port count and a scan knows neither. Defaulting those to "Unknown" to make the
/// button work would fill the CMDB with confident nonsense, so an incomplete approval is a 400 with
/// field errors instead.
/// </param>
/// <param name="EnrollMonitoring">
/// Whether to also create a monitored device. Off by default: discovering something is not a decision
/// to watch it, and an approval that silently enrolled every printer would fill the alert board.
/// </param>
public sealed record ApproveDiscoveredDeviceRequest(
    CiType? Type = null,
    string? Name = null,
    string? AssetTag = null,
    string? SerialNumber = null,
    string? Description = null,
    IReadOnlyDictionary<string, string?>? Attributes = null,
    Guid? CiId = null,
    bool EnrollMonitoring = false,
    string? PollerGroup = null,
    string? Note = null);

public sealed record RejectDiscoveredDeviceRequest(string? Note = null);

/// <summary>What discovery last observed about a CI, beside what the CMDB records for it.</summary>
public sealed record CiDiscoveryFactsResponse(
    Guid CiId,
    string Address,
    string? Hostname,
    bool RespondedToPing,
    IReadOnlyList<int> OpenPorts,
    DiscoveredSnmpResponse? Snmp,
    IReadOnlyList<DiscoveredNeighbourResponse> Neighbours,
    string DiscoveryName,
    string ScanProfileName,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int SightingCount);

public enum DiscoveryReviewOutcome
{
    Success,
    NotFound,

    /// <summary>The card has already been approved or rejected; a second decision is a 409, not an overwrite.</summary>
    AlreadyReviewed,

    Invalid,

    /// <summary>Creating the CI was refused by <c>ICiService</c> — a duplicate asset tag, a bad attribute.</summary>
    CiRejected,
}

public sealed record DiscoveryReviewResult(
    DiscoveryReviewOutcome Outcome,
    DiscoveredDeviceResponse? Device = null,
    Guid? CiId = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

/// <summary>
/// What one message did to the ledger. Returned by the intake so the consumer can log it and the
/// integration tests can assert on it without re-reading the database.
/// </summary>
public sealed record DiscoveryIntakeResult(
    Guid DiscoveredDeviceId,
    DiscoveredDeviceStatus Status,
    DiscoveryMatchRule Rule,
    Guid? CiId,
    bool IsNew);
