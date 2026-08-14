namespace Modules.Assets.Data;

/// <summary>
/// One stock take: somebody walking a site with a scanner, confirming that what the CMDB records is
/// physically there.
/// <para>
/// The session is the unit rather than the individual scan because the finding is an absence — the
/// asset nobody found — and an absence only exists relative to a list somebody agreed to walk. Without
/// the session there is no answer to "unscanned since when, out of what".
/// </para>
/// </summary>
public sealed class PhysicalAuditSession
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The site being walked, or null for the whole estate. Snapshotted by name beside the id for the
    /// same reason CI ownership is (WP-2.2): the directory belongs to Platform, this module may not
    /// join to it, and a closed session's report must stay readable after a site is renamed.
    /// </summary>
    public Guid? SiteId { get; set; }

    public string? SiteName { get; set; }

    public PhysicalAuditSessionStatus Status { get; set; } = PhysicalAuditSessionStatus.Open;

    public required string OpenedBy { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public string? ClosedBy { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Why the count was run, in the auditor's words.</summary>
    public string? Note { get; set; }

    public ICollection<PhysicalAuditScan> Scans { get; set; } = [];
}

public enum PhysicalAuditSessionStatus
{
    /// <summary>Being walked. Scans are accepted and the report moves as they arrive.</summary>
    Open,

    /// <summary>
    /// Finished. The report is what it was at the moment it closed, because nothing may be scanned into
    /// it afterwards — a stock take somebody can top up a week later counted nothing on the day.
    /// </summary>
    Closed,
}

/// <summary>
/// One asset physically confirmed during a session — the "scan" half of scan-to-confirm.
/// <para>
/// One row per CI per session: a rack walked by two people is not two sightings, and refusing the
/// second scan would make the second person believe the asset was missing. The re-scan is answered
/// with the row that already exists.
/// </para>
/// </summary>
public sealed class PhysicalAuditScan
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public PhysicalAuditSession? Session { get; set; }

    public Guid CiId { get; set; }

    public ConfigurationItem? Ci { get; set; }

    /// <summary>
    /// The CI's name as it stood when it was scanned. A snapshot for rendering only — the report reads
    /// everything it reasons about from the CI itself, because a CI renamed mid-count is still that CI.
    /// </summary>
    public required string CiName { get; set; }

    /// <summary>
    /// What was actually scanned or typed: a label URL, an asset tag, a serial number. Kept verbatim so
    /// a disputed count can be re-walked, and because it is the only evidence of <em>which</em> sticker
    /// somebody read.
    /// </summary>
    public required string Code { get; set; }

    public required string ScannedBy { get; set; }

    public DateTimeOffset ScannedAt { get; set; }

    public string? Note { get; set; }
}
