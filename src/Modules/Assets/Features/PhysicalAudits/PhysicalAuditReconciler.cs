using Modules.Assets.Data;

namespace Modules.Assets.Features.PhysicalAudits;

/// <summary>
/// One CI as the count sees it. Everything the classification depends on and nothing else, so the
/// rules can be exercised without a database.
/// </summary>
public sealed record AuditCandidate(
    Guid CiId,
    string Name,
    CiType Type,
    string? AssetTag,
    string? SerialNumber,
    CiLifecycleState LifecycleState,
    Guid? SiteId,
    string? SiteName,
    string? OwnerName);

/// <summary>One confirmed sighting, reduced to what the report reasons about.</summary>
public sealed record AuditScanFact(Guid CiId, string ScannedBy, DateTimeOffset ScannedAt);

public sealed record AuditReconciliation(
    IReadOnlyList<AuditCandidate> Scanned,
    IReadOnlyList<AuditCandidate> Unscanned,
    IReadOnlyList<(AuditCandidate Candidate, AuditUnexpectedReason Reason)> Unexpected);

/// <summary>
/// Turns a session's scans into the three lists a discrepancy report is: what was expected and found,
/// what was expected and never found, and what was found that nobody expected.
/// <para>
/// The interesting half is the third list. A count that only reported absences would miss the asset
/// sitting in a rack the CMDB says is at another site, or the one recorded as disposed — both of which
/// are the estate disagreeing with its record just as loudly as a missing machine.
/// </para>
/// <para>Pure: no database, no clock, no configuration. The whole matrix is unit-tested.</para>
/// </summary>
public static class PhysicalAuditReconciler
{
    /// <summary>
    /// The types a person can physically walk up to and scan.
    /// <para>
    /// Software, virtual and logical CIs are excluded because they are not in the room: listing every
    /// VM and every business service as "unscanned" would bury the laptop that is genuinely gone, and
    /// no sticker exists to clear them with.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<CiType> PhysicalTypes =
        [CiType.Hardware, CiType.Server, CiType.NetworkDevice];

    public static bool IsPhysical(CiType type) => PhysicalTypes.Contains(type);

    /// <summary>
    /// Whether a CI is on the list this session set out to walk.
    /// <para>
    /// Disposed is excluded, following WP-2.2: a disposed record states what the asset was when it left
    /// the estate, so expecting to find it would make every session report the same permanent absences.
    /// Retired is <em>not</em> excluded — a retired asset is usually still in the building waiting for
    /// collection, and that is exactly the pile a stock take is run to reconcile.
    /// </para>
    /// </summary>
    public static bool IsExpected(AuditCandidate candidate, Guid? scopeSiteId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return IsPhysical(candidate.Type)
            && candidate.LifecycleState != CiLifecycleState.Disposed
            && (scopeSiteId is null || candidate.SiteId == scopeSiteId);
    }

    /// <param name="scopeSiteId">The site being walked, or null for the whole estate.</param>
    /// <param name="candidates">
    /// Every CI in scope, plus every CI that was scanned — which is not the same set, because scanning
    /// something the scope did not include is precisely the finding the third list carries.
    /// </param>
    public static AuditReconciliation Reconcile(
        Guid? scopeSiteId,
        IReadOnlyList<AuditCandidate> candidates,
        IReadOnlySet<Guid> scannedCiIds)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(scannedCiIds);

        var scanned = new List<AuditCandidate>();
        var unscanned = new List<AuditCandidate>();
        var unexpected = new List<(AuditCandidate, AuditUnexpectedReason)>();

        foreach (var candidate in candidates)
        {
            var expected = IsExpected(candidate, scopeSiteId);
            var found = scannedCiIds.Contains(candidate.CiId);

            if (expected && found)
            {
                scanned.Add(candidate);
            }
            else if (expected)
            {
                unscanned.Add(candidate);
            }
            else if (found)
            {
                unexpected.Add((candidate, ReasonFor(candidate)));
            }

            // Neither expected nor found is not a finding: it is the rest of the estate.
        }

        return new AuditReconciliation(
            [.. scanned.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.CiId)],
            [.. unscanned.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.CiId)],
            [.. unexpected
                .OrderBy(entry => entry.Item1.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Item1.CiId)]);
    }

    /// <summary>
    /// Why a scanned asset was not expected, strongest claim first. A disposed CI at another site is
    /// reported as disposed: that the record says it left the estate is the more serious of the two,
    /// and an auditor chasing it will find the site discrepancy on the way.
    /// </summary>
    private static AuditUnexpectedReason ReasonFor(AuditCandidate candidate) =>
        !IsPhysical(candidate.Type) ? AuditUnexpectedReason.NotPhysical
        : candidate.LifecycleState == CiLifecycleState.Disposed ? AuditUnexpectedReason.Disposed
        : AuditUnexpectedReason.DifferentSite;
}
