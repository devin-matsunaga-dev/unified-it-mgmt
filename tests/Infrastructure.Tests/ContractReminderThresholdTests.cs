using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;

namespace Infrastructure.Tests;

/// <summary>
/// The planner decides which threshold a due date has crossed. These cover it being handed an
/// administrator's settings rather than the built-in 30/7/0.
/// </summary>
public sealed class ContractReminderThresholdTests
{
    private static readonly DateOnly Today = new(2026, 8, 21);

    private static ContractExpiryCandidate Due(int inDays) => new(
        ContractNotificationSubject.Contract,
        Guid.CreateVersion7(),
        "ProSupport",
        Today.AddDays(inDays),
        "owner@example.test");

    private static IReadOnlyList<ContractExpiryNotice> Plan(
        int dueInDays, IReadOnlyList<int>? thresholds = null) =>
        ContractExpiryPlanner.Plan([Due(dueInDays)], Today, new HashSet<ContractNotificationKey>(), thresholds);

    [Fact]
    public void Plan_WithNoSettings_KeepsTheBuiltInThresholds()
    {
        Assert.Equal(30, Assert.Single(Plan(30)).ThresholdDays);
        Assert.Empty(Plan(31));
    }

    /// <summary>The point of the feature: an administrator asking for notice further out.</summary>
    [Fact]
    public void Plan_WithWiderThresholds_NoticesEarlier()
    {
        var notices = Plan(90, [90, 60, 30]);

        Assert.Equal(90, Assert.Single(notices).ThresholdDays);
    }

    /// <summary>
    /// The tightest threshold a due date has crossed, not every one of them — otherwise a contract
    /// three days out would raise a notice for 90, 60, 30 and 7 on the same night.
    /// </summary>
    [Fact]
    public void Plan_ForADateInsideSeveralThresholds_RaisesOneNoticeForTheTightest()
    {
        var notices = Plan(3, [90, 60, 30, 7]);

        Assert.Equal(7, Assert.Single(notices).ThresholdDays);
    }

    [Fact]
    public void Plan_ForADateBeyondEveryThreshold_RaisesNothing()
    {
        Assert.Empty(Plan(120, [90, 60, 30]));
    }

    /// <summary>
    /// An empty set is how switching notices off reaches the planner. It has to mean "nothing is due"
    /// rather than "use the defaults", or turning them off would quietly turn them back on.
    /// </summary>
    [Fact]
    public void Plan_WithNoThresholdsAtAll_RaisesNothing()
    {
        Assert.Empty(Plan(1, []));
    }

    [Fact]
    public void Plan_OnTheExpiryDayItself_RaisesTheZeroThreshold()
    {
        Assert.Equal(0, Assert.Single(Plan(0, [30, 0])).ThresholdDays);
    }

    /// <summary>An overdue contract keeps raising the tightest threshold until somebody acts.</summary>
    [Fact]
    public void Plan_ForSomethingAlreadyExpired_StillRaisesTheTightestThreshold()
    {
        Assert.Equal(0, Assert.Single(Plan(-5, [30, 0])).ThresholdDays);
    }
}
