using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Modules.Assets.Features.Labels;

using Platform.Auditing;
using Platform.Directory;

namespace Modules.Assets.Features.PhysicalAudits;

/// <summary>
/// Runs a stock take: opens a session over a site, records what a scanner confirms, and answers the
/// question the whole workflow exists for — what did not turn up.
/// </summary>
public sealed class PhysicalAuditService(
    AssetsDbContext dbContext,
    ICiLabelService labelService,
    IDirectoryService directoryService,
    IAuditService auditService) : IPhysicalAuditService
{
    internal const int MaximumPageSize = 200;

    /// <summary>
    /// The most rows any one list of the report carries. A site with more assets than this is counted
    /// correctly — the totals are whole — and listed to the cut, because a report nobody can page
    /// through is a report nobody reads, and the number an auditor signs off is the count.
    /// </summary>
    internal const int MaximumReportItems = 500;

    private const string EntityType = "PhysicalAuditSession";

    public async Task<AuditSessionPageResponse> ListAsync(
        AuditSessionListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var query = dbContext.PhysicalAuditSessions.AsNoTracking();
        if (request.Status is { } status)
        {
            query = query.Where(session => session.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);

        // Open first, then newest: a walk somebody is part-way through is what they came here for.
        var items = await query
            .OrderBy(session => session.Status)
            .ThenByDescending(session => session.OpenedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(session => new AuditSessionSummaryResponse(
                session.Id,
                session.Name,
                session.SiteId,
                session.SiteName,
                session.Status,
                session.OpenedBy,
                session.OpenedAt,
                session.ClosedBy,
                session.ClosedAt,
                session.Note,
                session.Scans.Count))
            .ToListAsync(cancellationToken);

        return new AuditSessionPageResponse(items, total, page, pageSize);
    }

    public async Task<AuditSessionResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await dbContext.PhysicalAuditSessions.AsNoTracking()
            .FirstOrDefaultAsync(session => session.Id == id, cancellationToken);
        return session is null ? null : await SummariseAsync(session, cancellationToken);
    }

    public async Task<AuditDiscrepancyReportResponse?> GetReportAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await dbContext.PhysicalAuditSessions.AsNoTracking()
            .FirstOrDefaultAsync(session => session.Id == id, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var (reconciliation, scans) = await ReconcileAsync(session, cancellationToken);
        var scannedAt = scans.ToDictionary(scan => scan.CiId);

        return new AuditDiscrepancyReportResponse(
            Summarise(session, reconciliation),
            [.. reconciliation.Scanned.Take(MaximumReportItems).Select(item => ToItem(item, scannedAt))],
            [.. reconciliation.Unscanned.Take(MaximumReportItems).Select(item => ToItem(item, scannedAt))],
            [.. reconciliation.Unexpected.Take(MaximumReportItems)
                .Select(entry => ToUnexpected(entry.Candidate, entry.Reason, scannedAt))],
            reconciliation.Scanned.Count > MaximumReportItems
                || reconciliation.Unscanned.Count > MaximumReportItems
                || reconciliation.Unexpected.Count > MaximumReportItems,
            DateTimeOffset.UtcNow);
    }

    public async Task<AuditSessionResult> CreateAsync(
        CreateAuditSessionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        // The site is snapshotted by name here rather than read live, following WP-2.2's ownership
        // rule: the directory is Platform's, this module may not join to it, and a closed count has to
        // stay readable after somebody renames the building.
        string? siteName = null;
        if (request.SiteId is { } siteId)
        {
            var site = await directoryService.FindSiteAsync(siteId, cancellationToken);
            if (site is null)
            {
                return new AuditSessionResult(
                    AuditSessionOutcome.Invalid,
                    Errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["siteId"] = ["No site with that id exists."],
                    });
            }

            siteName = site.Name;
        }

        var session = new PhysicalAuditSession
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            SiteId = request.SiteId,
            SiteName = siteName,
            Status = PhysicalAuditSessionStatus.Open,
            OpenedBy = ActorName(actor),
            OpenedAt = DateTimeOffset.UtcNow,
            Note = Trim(request.Note),
        };

        dbContext.PhysicalAuditSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Opened", EntityType, session.Id.ToString(), null,
            new { session.Name, session.SiteId, session.SiteName, session.Note }, cancellationToken);

        return new AuditSessionResult(
            AuditSessionOutcome.Success, await SummariseAsync(session, cancellationToken));
    }

    public async Task<AuditSessionResult> RecordScanAsync(
        Guid sessionId,
        RecordAuditScanRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var session = await dbContext.PhysicalAuditSessions
            .FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new AuditSessionResult(AuditSessionOutcome.NotFound);
        }

        if (session.Status is PhysicalAuditSessionStatus.Closed)
        {
            return new AuditSessionResult(
                AuditSessionOutcome.Closed,
                Error: "This audit session was closed; a count that can be topped up later counted nothing on the day.");
        }

        var code = request.Code.Trim();

        // One resolver, shared with WP-2.7's scan page, so a stock take and a lookup can never disagree
        // about which asset a sticker names.
        var ci = await labelService.LookupAsync(code, cancellationToken);
        if (ci is null)
        {
            return new AuditSessionResult(
                AuditSessionOutcome.UnknownCode,
                Error: $"Nothing in the CMDB has the id, asset tag, or serial number '{code}'.");
        }

        var existing = await dbContext.PhysicalAuditScans
            .FirstOrDefaultAsync(scan => scan.SessionId == sessionId && scan.CiId == ci.Id, cancellationToken);
        if (existing is not null)
        {
            // Two people walking one rack is the normal case. Refusing the second scan would tell the
            // second person the asset was missing, which is the one wrong answer this workflow can give.
            return new AuditSessionResult(
                AuditSessionOutcome.Success,
                Scan: ToScanResponse(existing, ci, session, alreadyScanned: true));
        }

        var scan = new PhysicalAuditScan
        {
            Id = Guid.CreateVersion7(),
            SessionId = session.Id,
            CiId = ci.Id,
            CiName = ci.Name,
            Code = code,
            ScannedBy = ActorName(actor),
            ScannedAt = DateTimeOffset.UtcNow,
            Note = Trim(request.Note),
        };

        dbContext.PhysicalAuditScans.Add(scan);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Scanned", EntityType, session.Id.ToString(), null,
            new { scan.CiId, scan.CiName, scan.Code, scan.ScannedAt }, cancellationToken);

        return new AuditSessionResult(
            AuditSessionOutcome.Success, Scan: ToScanResponse(scan, ci, session, alreadyScanned: false));
    }

    public async Task<AuditSessionResult> RemoveScanAsync(
        Guid sessionId,
        Guid scanId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var session = await dbContext.PhysicalAuditSessions
            .FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new AuditSessionResult(AuditSessionOutcome.NotFound);
        }

        if (session.Status is PhysicalAuditSessionStatus.Closed)
        {
            return new AuditSessionResult(
                AuditSessionOutcome.Closed,
                Error: "This audit session was closed; its scans are the record of what was found on the day.");
        }

        var scan = await dbContext.PhysicalAuditScans
            .FirstOrDefaultAsync(scan => scan.Id == scanId && scan.SessionId == sessionId, cancellationToken);
        if (scan is null)
        {
            return new AuditSessionResult(AuditSessionOutcome.NotFound);
        }

        dbContext.PhysicalAuditScans.Remove(scan);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Undoing a mis-scan is audited as loudly as making one: it moves an asset from "found" back to
        // "missing", which is the finding somebody acts on.
        await auditService.WriteAsync(
            actor, "ScanRemoved", EntityType, session.Id.ToString(),
            new { scan.CiId, scan.CiName, scan.Code, scan.ScannedAt }, null, cancellationToken);

        return new AuditSessionResult(
            AuditSessionOutcome.Success, await SummariseAsync(session, cancellationToken));
    }

    public async Task<AuditSessionResult> CloseAsync(
        Guid sessionId,
        CloseAuditSessionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var session = await dbContext.PhysicalAuditSessions
            .FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new AuditSessionResult(AuditSessionOutcome.NotFound);
        }

        if (session.Status is PhysicalAuditSessionStatus.Closed)
        {
            return new AuditSessionResult(
                AuditSessionOutcome.Closed,
                Error: $"This audit session was already closed by {session.ClosedBy} on {session.ClosedAt:u}.");
        }

        var before = new { session.Status, session.ClosedBy, session.ClosedAt };
        session.Status = PhysicalAuditSessionStatus.Closed;
        session.ClosedBy = ActorName(actor);
        session.ClosedAt = DateTimeOffset.UtcNow;
        if (Trim(request.Note) is { } note)
        {
            session.Note = note;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Closed", EntityType, session.Id.ToString(), before,
            new { session.Status, session.ClosedBy, session.ClosedAt, session.Note }, cancellationToken);

        return new AuditSessionResult(
            AuditSessionOutcome.Success, await SummariseAsync(session, cancellationToken));
    }

    /// <summary>
    /// Loads both sides of a session — the CIs it set out to walk and the ones it actually found — and
    /// hands them to the pure reconciler.
    /// <para>
    /// The two sets are loaded separately on purpose. A scanned CI that is not in scope is exactly the
    /// finding the third list carries, so a query that only fetched the scope would drop it.
    /// </para>
    /// </summary>
    private async Task<(AuditReconciliation Reconciliation, IReadOnlyList<PhysicalAuditScan> Scans)> ReconcileAsync(
        PhysicalAuditSession session,
        CancellationToken cancellationToken)
    {
        var scans = await dbContext.PhysicalAuditScans.AsNoTracking()
            .Where(scan => scan.SessionId == session.Id)
            .ToListAsync(cancellationToken);
        var scannedIds = scans.Select(scan => scan.CiId).ToHashSet();

        // Narrowed in SQL to what the count could possibly be about — the physical types in scope — plus
        // whatever was scanned, whether or not it belongs. A scan that resolved to a virtual machine or
        // to a CI at another site is the finding, so it has to survive the filter that exists to keep
        // an estate-wide session from materialising the whole CMDB.
        var physicalTypes = PhysicalAuditReconciler.PhysicalTypes.ToArray();
        var query = dbContext.Cis.AsNoTracking()
            .Where(ci => scannedIds.Contains(ci.Id)
                || (physicalTypes.Contains(EF.Property<CiType>(ci, "CiType"))
                    && ci.LifecycleState != CiLifecycleState.Disposed
                    && (session.SiteId == null || ci.SiteId == session.SiteId)));

        var candidates = await query
            .Select(ci => new AuditCandidate(
                ci.Id,
                ci.Name,
                EF.Property<CiType>(ci, "CiType"),
                ci.AssetTag,
                ci.SerialNumber,
                ci.LifecycleState,
                ci.SiteId,
                ci.SiteName,
                ci.OwnerName))
            .ToListAsync(cancellationToken);

        return (PhysicalAuditReconciler.Reconcile(session.SiteId, candidates, scannedIds), scans);
    }

    private async Task<AuditSessionResponse> SummariseAsync(
        PhysicalAuditSession session,
        CancellationToken cancellationToken)
    {
        var (reconciliation, _) = await ReconcileAsync(session, cancellationToken);
        return Summarise(session, reconciliation);
    }

    private static AuditSessionResponse Summarise(
        PhysicalAuditSession session,
        AuditReconciliation reconciliation) =>
        new(
            session.Id,
            session.Name,
            session.SiteId,
            session.SiteName,
            session.Status,
            session.OpenedBy,
            session.OpenedAt,
            session.ClosedBy,
            session.ClosedAt,
            session.Note,
            reconciliation.Scanned.Count + reconciliation.Unscanned.Count,
            reconciliation.Scanned.Count,
            reconciliation.Unscanned.Count,
            reconciliation.Unexpected.Count);

    private static AuditItemResponse ToItem(
        AuditCandidate candidate,
        IReadOnlyDictionary<Guid, PhysicalAuditScan> scans)
    {
        var scan = scans.GetValueOrDefault(candidate.CiId);
        return new AuditItemResponse(
            candidate.CiId,
            candidate.Name,
            candidate.Type,
            candidate.AssetTag,
            candidate.SerialNumber,
            candidate.LifecycleState,
            candidate.SiteName,
            candidate.OwnerName,
            scan?.ScannedAt,
            scan?.ScannedBy);
    }

    private static AuditUnexpectedItemResponse ToUnexpected(
        AuditCandidate candidate,
        AuditUnexpectedReason reason,
        IReadOnlyDictionary<Guid, PhysicalAuditScan> scans)
    {
        var scan = scans[candidate.CiId];
        return new AuditUnexpectedItemResponse(
            candidate.CiId,
            candidate.Name,
            candidate.Type,
            candidate.AssetTag,
            candidate.SerialNumber,
            candidate.LifecycleState,
            candidate.SiteName,
            reason,
            scan.ScannedAt,
            scan.ScannedBy);
    }

    /// <summary>
    /// What the scan says about the asset the moment it is confirmed, so the handset can say "found,
    /// and it belongs at another site" without a second round trip.
    /// </summary>
    private static AuditScanResponse ToScanResponse(
        PhysicalAuditScan scan,
        Cis.CiResponse ci,
        PhysicalAuditSession session,
        bool alreadyScanned)
    {
        var candidate = new AuditCandidate(
            ci.Id, ci.Name, ci.Type, ci.AssetTag, ci.SerialNumber,
            ci.LifecycleState, ci.Ownership.SiteId, ci.Ownership.SiteName, ci.Ownership.OwnerName);
        var expected = PhysicalAuditReconciler.IsExpected(candidate, session.SiteId);

        return new AuditScanResponse(
            scan.Id,
            scan.SessionId,
            scan.CiId,
            ci.Name,
            ci.Type,
            ci.AssetTag,
            ci.SerialNumber,
            scan.Code,
            scan.ScannedBy,
            scan.ScannedAt,
            scan.Note,
            alreadyScanned,
            expected,
            expected
                ? null
                : !PhysicalAuditReconciler.IsPhysical(ci.Type) ? AuditUnexpectedReason.NotPhysical
                : ci.LifecycleState == CiLifecycleState.Disposed ? AuditUnexpectedReason.Disposed
                : AuditUnexpectedReason.DifferentSite);
    }

    private static string ActorName(ClaimsPrincipal actor) =>
        actor.Identity?.Name
        ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
