using Modules.Assets.Data;
using Modules.Assets.Features.Cis;
using Modules.Assets.Seeding;

namespace Infrastructure.Tests;

/// <summary>
/// The seeded estate is a hand-written table, so everything a database would otherwise catch late —
/// a mistyped attribute, a duplicate serial, an edge naming a CI that does not exist — is asserted
/// here, without infrastructure.
/// </summary>
public sealed class AssetsEstateTests
{
    [Fact]
    public void Cis_Estate_HoldsSixtyItemsCoveringEveryType()
    {
        Assert.Equal(AssetsInfrastructureSeeder.CiCount, AssetsEstate.Cis.Count);
        Assert.Equal(Enum.GetValues<CiType>().Length, AssetsEstate.Cis.Select(ci => ci.Type).Distinct().Count());
        Assert.Equal(
            Enum.GetValues<CiLifecycleState>().Length,
            AssetsEstate.Cis.Select(ci => ci.State).Distinct().Count());
    }

    /// <summary>
    /// The seeder writes typed rows through the DbContext rather than <c>ICiService</c>, so nothing at
    /// runtime enforces the per-type attributes. This is what does.
    /// </summary>
    [Fact]
    public void Cis_EveryItem_SatisfiesItsTypeSchema()
    {
        foreach (var seed in AssetsEstate.Cis)
        {
            var bound = CiTypeSchema.Bind(
                seed.Type,
                seed.Attributes.ToDictionary(entry => entry.Key, entry => (string?)entry.Value));

            Assert.True(bound.Errors.Count == 0, $"{seed.Key}: {string.Join("; ", bound.Errors.Keys)}");
        }
    }

    [Fact]
    public void Cis_KeysTagsAndSerials_AreUnique()
    {
        AssertUnique(AssetsEstate.Cis.Select(ci => ci.Key), "CI key");
        AssertUnique(AssetsEstate.Cis.Select(ci => ci.AssetTag).OfType<string>(), "asset tag");
        AssertUnique(AssetsEstate.Cis.Select(ci => ci.SerialNumber).OfType<string>(), "serial number");
        AssertUnique(AssetsEstate.Vendors.Select(vendor => vendor.Name), "vendor name");
        AssertUnique(AssetsEstate.Contracts.Select(contract => contract.Number), "contract number");
    }

    [Fact]
    public void Contracts_And_Coverage_ReferenceDeclaredRows()
    {
        var vendorKeys = AssetsEstate.Vendors.Select(vendor => vendor.Key).ToHashSet(StringComparer.Ordinal);
        var contractKeys = AssetsEstate.Contracts.Select(contract => contract.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(AssetsEstate.Contracts, contract => Assert.Contains(contract.VendorKey, vendorKeys));
        Assert.All(
            AssetsEstate.Cis.Select(ci => ci.ContractKey).OfType<string>(),
            key => Assert.Contains(key, contractKeys));
    }

    /// <summary>Every status the contract page can show has to be represented, or it cannot be verified.</summary>
    [Fact]
    public void Contracts_EndDates_CoverActiveExpiringAndExpired()
    {
        Assert.Contains(AssetsEstate.Contracts, contract => contract.EndInDays > 30);
        Assert.Contains(AssetsEstate.Contracts, contract => contract.EndInDays is > 7 and <= 30);
        Assert.Contains(AssetsEstate.Contracts, contract => contract.EndInDays is >= 0 and <= 7);
        Assert.Contains(AssetsEstate.Contracts, contract => contract.EndInDays < 0);
    }

    [Fact]
    public void Cis_WarrantyDates_CoverActiveExpiringAndExpired()
    {
        var warranties = AssetsEstate.Cis.Select(ci => ci.WarrantyInDays).OfType<int>().ToArray();

        Assert.Contains(warranties, days => days > 30);
        Assert.Contains(warranties, days => days is > 7 and <= 30);
        Assert.Contains(warranties, days => days is >= 0 and <= 7);
        Assert.Contains(warranties, days => days < 0);
    }

    [Fact]
    public void Relationships_EveryEdge_NamesTwoDifferentDeclaredCis()
    {
        var keys = AssetsEstate.Cis.Select(ci => ci.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in AssetsEstate.Relationships)
        {
            Assert.Contains(edge.SourceKey, keys);
            Assert.Contains(edge.TargetKey, keys);
            Assert.NotEqual(edge.SourceKey, edge.TargetKey);
        }

        // WP-2.3 refuses a duplicate (source, target, type) with a 409; the estate must not contain one.
        AssertUnique(
            AssetsEstate.Relationships.Select(edge => $"{edge.SourceKey}|{edge.TargetKey}|{edge.Type}"),
            "relationship");
    }

    /// <summary>A disposed CI refuses new relationships (WP-2.3), so the estate must not ask for one.</summary>
    [Fact]
    public void Relationships_NoEdge_TouchesADisposedCi()
    {
        var disposed = AssetsEstate.Cis
            .Where(ci => ci.State == CiLifecycleState.Disposed)
            .Select(ci => ci.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(disposed);
        Assert.DoesNotContain(
            AssetsEstate.Relationships,
            edge => disposed.Contains(edge.SourceKey) || disposed.Contains(edge.TargetKey));
    }

    /// <summary>
    /// The WP asks for two to three dependency trees. Walking what-needs-this from each site's router
    /// has to reach every CI in that site's tree, several hops down.
    /// </summary>
    [Theory]
    [InlineData("dc1-core-rtr-01", 5)]
    [InlineData("hq-edge-rtr-01", 3)]
    [InlineData("br1-rtr-01", 5)]
    public void Relationships_FromASiteRouter_FormAMultiLevelTree(string rootKey, int expectedDepth)
    {
        var depths = Descendants(rootKey);

        Assert.Equal(expectedDepth, depths.Values.Max());
        Assert.True(depths.Count >= 5, $"{rootKey} should carry a tree, but reached {depths.Count} CIs");
    }

    /// <summary>
    /// The three site trees are deliberately disjoint — no WAN edges — so each router's blast radius is
    /// a bounded answer. Together they must still account for every CI that has any edge at all.
    /// </summary>
    [Fact]
    public void Relationships_ThreeSiteTrees_ArePairwiseDisjointAndCoverEveryConnectedCi()
    {
        string[] roots = ["dc1-core-rtr-01", "hq-edge-rtr-01", "br1-rtr-01"];
        var trees = roots.Select(root => Descendants(root).Keys.Append(root).ToHashSet(StringComparer.Ordinal)).ToArray();

        for (var first = 0; first < trees.Length; first++)
        {
            for (var second = first + 1; second < trees.Length; second++)
            {
                Assert.Empty(trees[first].Intersect(trees[second], StringComparer.Ordinal));
            }
        }

        var connected = AssetsEstate.Relationships
            .SelectMany(edge => new[] { edge.SourceKey, edge.TargetKey })
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(connected.Except(trees.SelectMany(tree => tree), StringComparer.Ordinal));
    }

    /// <summary>
    /// Breadth-first "what needs this", the direction <c>impacted-by</c> walks: an edge reads
    /// source→target as "the source depends on the target", so a dependant is found by its target.
    /// </summary>
    private static Dictionary<string, int> Descendants(string rootKey)
    {
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new List<string> { rootKey };
        for (var depth = 1; frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var edge in AssetsEstate.Relationships.Where(edge => frontier.Contains(edge.TargetKey)))
            {
                if (edge.SourceKey == rootKey || depths.ContainsKey(edge.SourceKey))
                {
                    continue;
                }

                depths[edge.SourceKey] = depth;
                next.Add(edge.SourceKey);
            }

            frontier = next;
        }

        return depths;
    }

    private static void AssertUnique(IEnumerable<string> values, string label)
    {
        var duplicates = values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.True(duplicates.Length == 0, $"duplicate {label}: {string.Join(", ", duplicates)}");
    }
}
