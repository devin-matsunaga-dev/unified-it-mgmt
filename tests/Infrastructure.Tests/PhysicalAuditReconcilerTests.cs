using Modules.Assets.Data;
using Modules.Assets.Features.PhysicalAudits;

namespace Infrastructure.Tests;

/// <summary>
/// The rules a stock take reconciles by: what it set out to walk, what it found, and what it found
/// that nobody expected. No database — the classification is a pure function of the CI and the scope.
/// </summary>
public sealed class PhysicalAuditReconcilerTests
{
    private static readonly Guid Dc1 = Guid.CreateVersion7();
    private static readonly Guid Hq = Guid.CreateVersion7();

    /// <summary>The WP's own verification step: three scanned, and the report lists the unscanned.</summary>
    [Fact]
    public void Reconcile_WithThreeOfFiveConfirmed_ListsTheTwoThatDidNotTurnUp()
    {
        var estate = Enumerable.Range(1, 5).Select(index => Candidate($"Switch {index}", Dc1)).ToList();
        var scanned = Found([.. estate.Take(3).Select(candidate => candidate.CiId)]);

        var reconciliation = PhysicalAuditReconciler.Reconcile(Dc1, estate, scanned);

        Assert.Equal(3, reconciliation.Scanned.Count);
        Assert.Equal(["Switch 4", "Switch 5"], reconciliation.Unscanned.Select(item => item.Name));
        Assert.Empty(reconciliation.Unexpected);
    }

    /// <summary>
    /// A count that only reported absences would miss the machine sitting in a rack the CMDB says is
    /// at another site — the estate disagreeing with its record just as loudly as a missing one.
    /// </summary>
    [Fact]
    public void Reconcile_WhenSomethingRecordedElsewhereIsScannedHere_IsUnexpectedAndNotCounted()
    {
        var here = Candidate("Rack switch", Dc1);
        var stranger = Candidate("Laptop from Head Office", Hq);

        var reconciliation = PhysicalAuditReconciler.Reconcile(Dc1, [here, stranger], Found(here.CiId, stranger.CiId));

        Assert.Equal([here.Name], reconciliation.Scanned.Select(item => item.Name));
        var (candidate, reason) = Assert.Single(reconciliation.Unexpected);
        Assert.Equal(stranger.Name, candidate.Name);
        Assert.Equal(AuditUnexpectedReason.DifferentSite, reason);
    }

    /// <summary>
    /// A disposed record says the asset left the estate. Finding it in the building is the discrepancy;
    /// expecting to find it would make every count report the same permanent absences.
    /// </summary>
    [Fact]
    public void Reconcile_ForADisposedAsset_ExpectsItNowhereAndFlagsItIfItTurnsUp()
    {
        var disposed = Candidate("Scrapped printer", Dc1) with { LifecycleState = CiLifecycleState.Disposed };

        var unscanned = PhysicalAuditReconciler.Reconcile(Dc1, [disposed], Found());
        var scanned = PhysicalAuditReconciler.Reconcile(Dc1, [disposed], Found(disposed.CiId));

        Assert.Empty(unscanned.Unscanned);
        Assert.Empty(unscanned.Unexpected);
        Assert.Equal(AuditUnexpectedReason.Disposed, Assert.Single(scanned.Unexpected).Reason);
    }

    /// <summary>
    /// A retired asset is usually still in the building waiting for collection — exactly the pile a
    /// stock take is run to reconcile — so it stays on the list.
    /// </summary>
    [Fact]
    public void Reconcile_ForARetiredAsset_StillExpectsToFindIt()
    {
        var retired = Candidate("Withdrawn workstation", Dc1) with { LifecycleState = CiLifecycleState.Retired };

        var reconciliation = PhysicalAuditReconciler.Reconcile(Dc1, [retired], Found());

        Assert.Equal([retired.Name], reconciliation.Unscanned.Select(item => item.Name));
    }

    /// <summary>
    /// Nobody can walk up to a business service. Listing every VM and every service as unscanned would
    /// bury the laptop that is genuinely gone, and no sticker exists to clear them with.
    /// </summary>
    [Theory]
    [InlineData(CiType.Virtual)]
    [InlineData(CiType.Software)]
    [InlineData(CiType.Logical)]
    public void Reconcile_ForSomethingWithNoPhysicalPresence_IsNeverExpected(CiType type)
    {
        var intangible = Candidate("Payroll service", Dc1) with { Type = type };

        var reconciliation = PhysicalAuditReconciler.Reconcile(Dc1, [intangible], Found());

        Assert.Empty(reconciliation.Unscanned);
        Assert.Empty(reconciliation.Scanned);
    }

    [Fact]
    public void Reconcile_WhenAVirtualCiIsScannedByHand_IsReportedRatherThanSilentlyDropped()
    {
        var virtualCi = Candidate("Finance ERP VM", Dc1) with { Type = CiType.Virtual };

        var reconciliation = PhysicalAuditReconciler.Reconcile(Dc1, [virtualCi], Found(virtualCi.CiId));

        Assert.Equal(AuditUnexpectedReason.NotPhysical, Assert.Single(reconciliation.Unexpected).Reason);
    }

    /// <summary>
    /// An estate-wide count expects everything physical wherever it lives, so "recorded at another
    /// site" cannot be a finding — there is no other site.
    /// </summary>
    [Fact]
    public void Reconcile_WithNoSiteScope_ExpectsEveryPhysicalCiWhereverItIsRecorded()
    {
        var here = Candidate("Rack switch", Dc1);
        var elsewhere = Candidate("Branch switch", Hq);

        var reconciliation = PhysicalAuditReconciler.Reconcile(null, [here, elsewhere], Found(elsewhere.CiId));

        Assert.Equal([elsewhere.Name], reconciliation.Scanned.Select(item => item.Name));
        Assert.Equal([here.Name], reconciliation.Unscanned.Select(item => item.Name));
        Assert.Empty(reconciliation.Unexpected);
    }

    /// <summary>The rest of the estate is not a finding: neither expected nor found is simply absent.</summary>
    [Fact]
    public void Reconcile_ForACiThatIsNeitherInScopeNorScanned_ReportsItNowhere()
    {
        var elsewhere = Candidate("Head Office printer", Hq);

        var reconciliation = PhysicalAuditReconciler.Reconcile(Dc1, [elsewhere], Found());

        Assert.Empty(reconciliation.Scanned);
        Assert.Empty(reconciliation.Unscanned);
        Assert.Empty(reconciliation.Unexpected);
    }

    private static HashSet<Guid> Found(params Guid[] ciIds) => [.. ciIds];

    private static AuditCandidate Candidate(string name, Guid siteId) =>
        new(Guid.CreateVersion7(), name, CiType.NetworkDevice, $"NET-{name.GetHashCode(StringComparison.Ordinal):X6}",
            "SERIAL", CiLifecycleState.Deployed, siteId, "Primary Data Centre", "Technician Two");
}
