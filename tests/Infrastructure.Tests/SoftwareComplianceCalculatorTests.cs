using Modules.Assets.Features.Contracts;
using Modules.Assets.Features.Software;

namespace Infrastructure.Tests;

/// <summary>
/// Installed versus entitled, without a database. The two states that are easy to get backwards are
/// Unlicensed (nothing was ever bought) and OverDeployed (something was, and it is not enough).
/// </summary>
public sealed class SoftwareComplianceCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 13);

    [Theory]
    // installed, pools, live pools, entitled → state
    [InlineData(5, 1, 1, 3, SoftwareComplianceState.OverDeployed)]
    [InlineData(3, 1, 1, 3, SoftwareComplianceState.Compliant)]
    [InlineData(1, 1, 1, 3, SoftwareComplianceState.Compliant)]
    [InlineData(0, 1, 1, 3, SoftwareComplianceState.Unused)]
    [InlineData(5, 0, 0, 0, SoftwareComplianceState.Unlicensed)]
    [InlineData(0, 0, 0, 0, SoftwareComplianceState.Compliant)]
    public void State_ForOneProduct_ReadsItsInstallsAgainstItsLivePools(
        int installedCiCount,
        int poolCount,
        int livePoolCount,
        int entitled,
        SoftwareComplianceState expected) =>
        Assert.Equal(
            expected,
            SoftwareComplianceCalculator.State(new(installedCiCount, poolCount, livePoolCount, entitled)));

    /// <summary>
    /// A pool that exists but entitles nothing is still a pool: the product was bought, so this is an
    /// over-deployment rather than a product nobody licensed.
    /// </summary>
    [Fact]
    public void State_APoolOfZeroSeats_IsOverDeployedRatherThanUnlicensed() =>
        Assert.Equal(
            SoftwareComplianceState.OverDeployed,
            SoftwareComplianceCalculator.State(new(2, 1, 1, 0)));

    /// <summary>
    /// The distinction the two counts exist for: a licence that lapsed is not a licence nobody bought.
    /// The report shows the expired-pool count beside it, so the reason is on the same row.
    /// </summary>
    [Fact]
    public void State_AProductWhoseOnlyPoolHasExpired_IsOverDeployedRatherThanUnlicensed()
    {
        Assert.Equal(
            SoftwareComplianceState.OverDeployed,
            SoftwareComplianceCalculator.State(new(2, PoolCount: 1, LivePoolCount: 0, Entitled: 0)));

        // …and with nothing installed there is nothing to be short of, so it is not "unused" either.
        Assert.Equal(
            SoftwareComplianceState.Compliant,
            SoftwareComplianceCalculator.State(new(0, PoolCount: 1, LivePoolCount: 0, Entitled: 0)));
    }

    [Fact]
    public void Overage_IsPositiveWhenShortAndNegativeWhenSpare()
    {
        Assert.Equal(2, new SoftwareComplianceTally(5, 1, 1, 3).Overage);
        Assert.Equal(-2, new SoftwareComplianceTally(3, 1, 1, 5).Overage);
        Assert.Equal(0, new SoftwareComplianceTally(3, 1, 1, 3).Overage);
    }

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, -1, false)]
    [InlineData(false, 100, false)]
    public void IsLive_ExcludesAnExpiredOrDeactivatedPool(bool isActive, int expiresInDays, bool expected) =>
        Assert.Equal(expected, SoftwareComplianceCalculator.IsLive(isActive, Today.AddDays(expiresInDays), Today));

    /// <summary>A perpetual licence has no end date, so it is always live and has no expiry status.</summary>
    [Fact]
    public void APerpetualPool_IsAlwaysLiveAndHasNoStatus()
    {
        Assert.True(SoftwareComplianceCalculator.IsLive(true, null, Today));
        Assert.Null(SoftwareComplianceCalculator.Status(null, Today));
        Assert.False(SoftwareComplianceCalculator.IsLive(false, null, Today));
    }

    [Theory]
    [InlineData(45, ContractExpiryStatus.Active)]
    [InlineData(30, ContractExpiryStatus.ExpiringSoon)]
    [InlineData(0, ContractExpiryStatus.ExpiringSoon)]
    [InlineData(-1, ContractExpiryStatus.Expired)]
    public void Status_UsesTheSameThirtyDaysAsEveryOtherDatedThingInTheModule(
        int expiresInDays,
        ContractExpiryStatus expected) =>
        Assert.Equal(expected, SoftwareComplianceCalculator.Status(Today.AddDays(expiresInDays), Today));
}
