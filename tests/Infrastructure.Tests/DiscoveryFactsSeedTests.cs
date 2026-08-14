using Modules.Assets.Data;
using Modules.Assets.Features.Drift;
using Modules.Assets.Seeding;

namespace Infrastructure.Tests;

/// <summary>
/// The seeded observations, run through the real comparator without a database.
/// <para>
/// It exists for the same reason <c>SoftwareCatalogSeedTests</c> does: on a fresh <c>aspire run</c>
/// the drift report has to open on findings rather than on an empty table, and the only thing standing
/// between "it does" and "somebody edited a fixture" is a test that fails when the two hand-written
/// tables — the estate and the observations — stop disagreeing in the ways they were written to.
/// </para>
/// </summary>
public sealed class DiscoveryFactsSeedTests
{
    /// <summary>
    /// Mirrors <c>Platform.Seeding.DemoDataSeeder</c>'s sites by hand, because that array is private
    /// and this test's claim is about two fixtures agreeing rather than about the seeder's plumbing.
    /// If a site is ever renamed there, this test fails — which is the right place to find out.
    /// </summary>
    private static readonly Dictionary<string, string> SiteNames = new(StringComparer.Ordinal)
    {
        ["HQ"] = "Head Office",
        ["DC1"] = "Primary Data Centre",
        ["BR1"] = "Regional Branch",
    };

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void EveryObservation_NamesACiTheEstateSeeds()
    {
        var keys = AssetsEstate.Cis.Select(ci => ci.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(DiscoveryFactsSeeder.ObservedCiKeys, key => Assert.Contains(key, keys));
    }

    [Fact]
    public void TheSeededObservations_PutOneOfEveryFindingOnTheFirstScreen()
    {
        var findings = AnalyseFixture();

        // The WP's own verification case, pre-baked: a switch racked in the data centre while the CMDB
        // still says Head Office.
        Assert.Contains(findings, finding =>
            finding.CiKey == "hq-acc-sw-01"
            && finding.Finding.Field == DriftFields.Location
            && finding.Finding.Kind == DriftFindingKind.Changed);

        // Re-addressed during a subnet change: the field somebody will try to poll and fail.
        Assert.Contains(findings, finding =>
            finding.CiKey == "hq-acc-sw-02"
            && finding.Finding.Field == DriftFields.ManagementIp
            && finding.Finding.Kind == DriftFindingKind.Changed);

        // Rebuilt and renamed, and nobody told the CMDB.
        Assert.Contains(findings, finding =>
            finding.CiKey == "dc1-esx-01"
            && finding.Finding.Field == DriftFields.Hostname
            && finding.Finding.Kind == DriftFindingKind.Changed);

        // Answered SNMP and left sysLocation empty — the network has nothing to say rather than
        // something contradictory.
        Assert.Contains(findings, finding =>
            finding.CiKey == "dc1-core-rtr-01"
            && finding.Finding.Field == DriftFields.Location
            && finding.Finding.Kind == DriftFindingKind.Missing);

        // The other shape of missing: the record is still here and the thing stopped answering.
        Assert.Contains(findings, finding =>
            finding.CiKey == "br1-sw-01"
            && finding.Finding.Field == DriftFields.LastSeen
            && finding.Finding.Kind == DriftFindingKind.Missing);
    }

    /// <summary>
    /// The demo needs a CI that agrees about everything as much as it needs ones that do not — a report
    /// where every observed CI is a finding teaches an operator that the report is noise.
    /// </summary>
    [Fact]
    public void TheCoreSwitch_AgreesWithTheCmdbAboutEverything()
    {
        Assert.DoesNotContain(AnalyseFixture(), finding => finding.CiKey == "dc1-core-sw-01");
    }

    /// <summary>
    /// One neighbour is a relationship the estate records and one is not, and it is the second that
    /// makes the "cables nobody recorded" section non-empty on a fresh run. If the estate ever gains an
    /// edge between the two core switches, this fixture stops demonstrating anything.
    /// </summary>
    [Fact]
    public void TheSeededNeighbours_AreOneRecordedCableAndOneNobodyWroteDown()
    {
        var edges = AssetsEstate.Relationships
            .Select(edge => Pair(edge.SourceKey, edge.TargetKey))
            .ToHashSet();

        Assert.Contains(Pair("dc1-core-sw-01", "dc1-core-rtr-01"), edges);
        Assert.DoesNotContain(Pair("dc1-core-sw-01", "dc1-core-sw-02"), edges);
    }

    /// <summary>
    /// Every neighbour has to resolve to a CI, or the fold that turns a report into a link never fires
    /// and the section stays empty for a reason nobody can see. Both are named by their own sysName,
    /// which the fixture also seeds — the strongest rung of WP-4.3's ladder.
    /// </summary>
    [Fact]
    public void EveryNeighbourTheFixtureReports_IsItselfObserved()
    {
        var observed = DiscoveryFactsSeeder.ObservedValues
            .Select(entry => entry.SysName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("dc1-core-rtr-01", observed);
        Assert.Contains("dc1-core-sw-02", observed);
    }

    private static IReadOnlyList<(string CiKey, DriftFinding Finding)> AnalyseFixture()
    {
        var estate = AssetsEstate.Cis.ToDictionary(ci => ci.Key, StringComparer.Ordinal);

        return
        [
            .. DiscoveryFactsSeeder.ObservedValues.SelectMany(observation =>
            {
                var seed = estate[observation.CiKey];
                var subject = new DriftSubject(
                    Guid.CreateVersion7(),
                    seed.Name,
                    seed.Type,
                    Guid.CreateVersion7(),
                    seed.SiteCode is null ? null : SiteNames[seed.SiteCode],
                    DriftAnalyzer.RecordsHostname(seed.Type)
                        ? seed.Attributes.GetValueOrDefault("hostname", string.Empty)
                        : null,
                    DriftAnalyzer.RecordsManagementIp(seed.Type)
                        ? seed.Attributes.GetValueOrDefault("managementIp", string.Empty)
                        : null,
                    new DriftObservation(
                        observation.Address,
                        null,
                        observation.SysName,
                        observation.SysLocation,
                        "Seeded observation",
                        AnsweredSnmp: true,
                        LastSeenAt: Now.AddDays(-observation.LastSeenDaysAgo)));

                return DriftAnalyzer
                    .Analyse(subject, Now, DriftAnalyzer.DefaultStaleAfterDays)
                    .Select(finding => (observation.CiKey, finding));
            }),
        ];
    }

    private static (string, string) Pair(string first, string second) =>
        string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);
}
