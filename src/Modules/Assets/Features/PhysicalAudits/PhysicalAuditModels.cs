using Modules.Assets.Data;

namespace Modules.Assets.Features.PhysicalAudits;

public sealed record CreateAuditSessionRequest(string Name, Guid? SiteId = null, string? Note = null);

/// <param name="Code">
/// Whatever the scanner produced: one of our own label URLs, a bare CI id, an asset tag, or a serial
/// number. Resolved exactly as WP-2.7's <c>/scan</c> page resolves it, so a stock take and a lookup
/// can never disagree about which asset a sticker names.
/// </param>
public sealed record RecordAuditScanRequest(string Code, string? Note = null);

public sealed record CloseAuditSessionRequest(string? Note = null);

public sealed record AuditSessionListRequest(
    PhysicalAuditSessionStatus? Status = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>
/// A session as the list renders it. It carries the number of scans and not the reconciled counts,
/// deliberately: working out how many assets a session still owes means classifying every CI in its
/// scope, and doing that once per row would make opening the list the most expensive read in the
/// module. The counts live on the session's own page, which is where somebody is asking.
/// </summary>
public sealed record AuditSessionSummaryResponse(
    Guid Id,
    string Name,
    Guid? SiteId,
    string? SiteName,
    PhysicalAuditSessionStatus Status,
    string OpenedBy,
    DateTimeOffset OpenedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    string? Note,
    int ScanCount);

/// <summary>One session, reconciled: what it set out to walk against what it found.</summary>
public sealed record AuditSessionResponse(
    Guid Id,
    string Name,
    Guid? SiteId,
    string? SiteName,
    PhysicalAuditSessionStatus Status,
    string OpenedBy,
    DateTimeOffset OpenedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    string? Note,
    int ExpectedCount,
    int ScannedCount,
    int UnscannedCount,
    int UnexpectedCount);

public sealed record AuditSessionPageResponse(
    IReadOnlyList<AuditSessionSummaryResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>One asset the count expected to find, or found.</summary>
public sealed record AuditItemResponse(
    Guid CiId,
    string Name,
    CiType Type,
    string? AssetTag,
    string? SerialNumber,
    CiLifecycleState LifecycleState,
    string? SiteName,
    string? OwnerName,
    DateTimeOffset? ScannedAt,
    string? ScannedBy);

/// <summary>Why an asset that was scanned was not on the list the session set out to walk.</summary>
public enum AuditUnexpectedReason
{
    /// <summary>The CMDB records it at another site — an asset that moved and nobody said so.</summary>
    DifferentSite = 1,

    /// <summary>
    /// The CMDB records it as disposed. It is in the building and the record says it left the estate,
    /// which is the discrepancy a stock take exists to catch.
    /// </summary>
    Disposed = 2,

    /// <summary>
    /// Not a physical asset at all — a software, virtual or logical CI. Reachable only by typing an id,
    /// and reported rather than silently dropped because a scanner that resolves to one is a mistake
    /// somebody wants to see.
    /// </summary>
    NotPhysical = 3,
}

public sealed record AuditUnexpectedItemResponse(
    Guid CiId,
    string Name,
    CiType Type,
    string? AssetTag,
    string? SerialNumber,
    CiLifecycleState LifecycleState,
    string? SiteName,
    AuditUnexpectedReason Reason,
    DateTimeOffset ScannedAt,
    string ScannedBy);

/// <param name="Truncated">
/// True when the estate in scope has more assets than the report will list. The counts above it are
/// still whole — only the rows are cut — because a truncated answer must never look like a complete
/// one (WP-2.4), and the count is the number an auditor signs off.
/// </param>
public sealed record AuditDiscrepancyReportResponse(
    AuditSessionResponse Session,
    IReadOnlyList<AuditItemResponse> Scanned,
    IReadOnlyList<AuditItemResponse> Unscanned,
    IReadOnlyList<AuditUnexpectedItemResponse> Unexpected,
    bool Truncated,
    DateTimeOffset GeneratedAt);

/// <param name="AlreadyScanned">
/// True when this asset had already been confirmed in this session. Not an error: two people walking
/// one rack is the normal case, and a refusal would tell the second one the asset was missing.
/// </param>
public sealed record AuditScanResponse(
    Guid Id,
    Guid SessionId,
    Guid CiId,
    string CiName,
    CiType CiType,
    string? AssetTag,
    string? SerialNumber,
    string Code,
    string ScannedBy,
    DateTimeOffset ScannedAt,
    string? Note,
    bool AlreadyScanned,
    bool Expected,
    AuditUnexpectedReason? UnexpectedReason);

public enum AuditSessionOutcome
{
    Success,
    NotFound,

    /// <summary>The session is closed. Nothing may be scanned into it and it cannot be closed twice.</summary>
    Closed,

    /// <summary>The code named no CI. A 404 on the code rather than on the session.</summary>
    UnknownCode,

    Invalid,
}

public sealed record AuditSessionResult(
    AuditSessionOutcome Outcome,
    AuditSessionResponse? Session = null,
    AuditScanResponse? Scan = null,
    string? Error = null,
    IDictionary<string, string[]>? Errors = null);
