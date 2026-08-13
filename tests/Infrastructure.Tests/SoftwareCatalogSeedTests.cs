using Modules.Assets.Features.Software;
using Modules.Assets.Seeding;

namespace Infrastructure.Tests;

/// <summary>
/// The seeded software fixture, asserted without a database. It has one job beyond existing: on a
/// fresh <c>aspire run</c>, every compliance state the report can produce must be on the screen —
/// including the WP's own verification case, a pool of three against five installs.
/// </summary>
public sealed class SoftwareCatalogSeedTests
{
    private static readonly IReadOnlyList<SoftwareRule> Rules =
    [
        .. SoftwareCatalogSeeder.Products.Select((product, index) => new SoftwareRule(
            ProductId(index), product.MatchKind, SoftwareNormaliser.Canonicalise(product.Pattern), 0))
    ];

    [Fact]
    public void EverySeededPool_NamesAProductTheCatalogueSeeds()
    {
        var keys = SoftwareCatalogSeeder.Products.Select(product => product.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(SoftwareCatalogSeeder.LicensePools, pool => Assert.Contains(pool.ProductKey, keys));
    }

    [Fact]
    public void EverySeededProduct_HasAUniquePublisherAndNameAndAUniquePattern()
    {
        Assert.Equal(
            SoftwareCatalogSeeder.Products.Count,
            SoftwareCatalogSeeder.Products.Select(product => (product.Publisher, product.Name)).Distinct().Count());
        Assert.Equal(
            SoftwareCatalogSeeder.Products.Count,
            SoftwareCatalogSeeder.Products.Select(product => (product.MatchKind, product.Pattern)).Distinct().Count());
    }

    /// <summary>
    /// The catalogue has to actually claim the raw names the fixture reports, or the seeded estate
    /// opens on a compliance report of nothing but unrecognised strings.
    /// </summary>
    [Fact]
    public void EverySeededInstall_NormalisesExceptTheOneDeliberatelyLeftUnrecognised()
    {
        var unmatched = SoftwareCatalogSeeder.Installs
            .Where(install => SoftwareNormaliser.Match(install.RawName, Rules) is null)
            .Select(install => install.RawName)
            .Distinct()
            .ToList();

        // Exactly one, and it is the one the "add a rule, re-normalise" demo is performed against.
        Assert.Equal(["Contoso VPN Client"], unmatched);
    }

    [Fact]
    public void TheSeededInstalls_CoverFiveMachinesAndNeverRepeatOneOnAMachine()
    {
        var machines = SoftwareCatalogSeeder.Installs.Select(install => install.CiKey).Distinct().ToList();

        Assert.Equal(5, machines.Count);
        Assert.Equal(
            SoftwareCatalogSeeder.Installs.Count,
            SoftwareCatalogSeeder.Installs
                .Select(install => (install.CiKey, Key: SoftwareNormaliser.IdentityKeyFor(install.RawName, install.Version)))
                .Distinct()
                .Count());
    }

    /// <summary>The WP's own verification case, standing up before anybody runs anything.</summary>
    [Fact]
    public void TheAcrobatPool_IsThreeSeatsAgainstFiveInstalls()
    {
        var acrobat = Product("acrobat");
        var pool = Assert.Single(SoftwareCatalogSeeder.LicensePools, item => item.ProductKey == "acrobat");
        var installedOn = SoftwareCatalogSeeder.Installs
            .Where(install => SoftwareNormaliser.Match(install.RawName, Rules) == acrobat)
            .Select(install => install.CiKey)
            .Distinct()
            .Count();

        Assert.Equal(3, pool.Entitlements);
        Assert.Equal(5, installedOn);
    }

    [Fact]
    public void TheSeededFixture_ProducesEveryComplianceState()
    {
        Assert.Equal(SoftwareComplianceState.OverDeployed, StateOf("acrobat"));
        Assert.Equal(SoftwareComplianceState.Compliant, StateOf("office"));
        Assert.Equal(SoftwareComplianceState.Unused, StateOf("zoom"));
        // Installed on every laptop with nothing bought for it, which is the ordinary case for a browser.
        Assert.Equal(SoftwareComplianceState.Unlicensed, StateOf("chrome"));
    }

    /// <summary>
    /// One pool has to lapse inside the 7-day window, or the renewal half of this package has nothing
    /// true to raise on a fresh database and reads as a feature that does not work.
    /// </summary>
    [Fact]
    public void OneSeededPool_ExpiresInsideTheNoticeWindowAndOneIsPerpetual()
    {
        Assert.Contains(SoftwareCatalogSeeder.LicensePools, pool => pool.ExpiresInDays is > 0 and <= 7);
        Assert.Contains(SoftwareCatalogSeeder.LicensePools, pool => pool.ExpiresInDays is null);
    }

    private static SoftwareComplianceState StateOf(string productKey)
    {
        var productId = Product(productKey);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var installedCiCount = SoftwareCatalogSeeder.Installs
            .Where(install => SoftwareNormaliser.Match(install.RawName, Rules) == productId)
            .Select(install => install.CiKey)
            .Distinct()
            .Count();
        var pools = SoftwareCatalogSeeder.LicensePools.Where(pool => pool.ProductKey == productKey).ToList();
        var live = pools
            .Where(pool => SoftwareComplianceCalculator.IsLive(
                true, pool.ExpiresInDays is { } days ? today.AddDays(days) : null, today))
            .ToList();

        return SoftwareComplianceCalculator.State(
            new(installedCiCount, pools.Count, live.Count, live.Sum(pool => pool.Entitlements)));
    }

    private static Guid Product(string key) =>
        ProductId(SoftwareCatalogSeeder.Products.Select((product, index) => (product.Key, index))
            .Single(entry => entry.Key == key).index);

    /// <summary>Mirrors the seeder's own scheme so the rules here point at the same ids it writes.</summary>
    private static Guid ProductId(int index) =>
        Guid.Parse($"01980002-0009-7000-8000-{index:0000}{0:00000000}");
}
