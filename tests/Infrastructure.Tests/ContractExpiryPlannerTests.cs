using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;

namespace Infrastructure.Tests;

/// <summary>
/// The 30/7/0 rule without a clock or a database: which notice a due date earns on a given day, and
/// why a second run of the same day is silent.
/// </summary>
public sealed class ContractExpiryPlannerTests
{
    private static readonly DateOnly Today = new(2026, 8, 8);

    [Theory]
    [InlineData(31, null)]
    [InlineData(45, null)]
    [InlineData(30, 30)]
    [InlineData(29, 30)]
    [InlineData(8, 30)]
    [InlineData(7, 7)]
    [InlineData(1, 7)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(-400, 0)]
    public void Plan_ForOneDueDate_RaisesTheTightestCrossedThreshold(int daysRemaining, int? expectedThreshold)
    {
        var candidate = CandidateDueIn(daysRemaining);

        var notices = ContractExpiryPlanner.Plan([candidate], Today, Sent());

        if (expectedThreshold is null)
        {
            Assert.Empty(notices);
            return;
        }

        var notice = Assert.Single(notices);
        Assert.Equal(expectedThreshold, notice.ThresholdDays);
        Assert.Equal(daysRemaining, notice.DaysRemaining);
    }

    /// <summary>
    /// A job that has not run for weeks must not fire 30, 7 and 0 at once — it says one thing, at the
    /// threshold that is actually true today.
    /// </summary>
    [Fact]
    public void Plan_WhenSeveralThresholdsWereCrossedUnobserved_RaisesOnlyOne()
    {
        var notice = Assert.Single(ContractExpiryPlanner.Plan([CandidateDueIn(3)], Today, Sent()));

        Assert.Equal(7, notice.ThresholdDays);
    }

    [Fact]
    public void Plan_WhenTheThresholdWasAlreadyRaised_IsSilent()
    {
        var candidate = CandidateDueIn(7);
        var alreadySent = Sent(new ContractNotificationKey(
            candidate.Subject, candidate.SubjectId, candidate.DueDate, 7));

        Assert.Empty(ContractExpiryPlanner.Plan([candidate], Today, alreadySent));
    }

    /// <summary>Each threshold is raised once over the life of a due date, walking day by day.</summary>
    [Fact]
    public void Plan_RunDailyAcrossTheWholeWindow_RaisesExactlyThreeNotices()
    {
        var candidate = CandidateDueIn(40);
        var sent = new HashSet<ContractNotificationKey>();
        var raised = new List<int>();

        for (var day = 0; day <= 45; day++)
        {
            foreach (var notice in ContractExpiryPlanner.Plan([candidate], Today.AddDays(day), sent))
            {
                raised.Add(notice.ThresholdDays);
                sent.Add(notice.Key);
            }
        }

        Assert.Equal([30, 7, 0], raised);
    }

    /// <summary>
    /// Renewing a contract moves its end date, and the new date is a new cycle: the notices raised
    /// against the old one must not silence it.
    /// </summary>
    [Fact]
    public void Plan_WhenTheDueDateMoves_StartsAFreshCycle()
    {
        var original = CandidateDueIn(7);
        var sent = Sent(new ContractNotificationKey(
            original.Subject, original.SubjectId, original.DueDate, 7));
        var renewed = original with { DueDate = Today.AddDays(5) };

        var notice = Assert.Single(ContractExpiryPlanner.Plan([renewed], Today, sent));

        Assert.Equal(7, notice.ThresholdDays);
        Assert.Equal(renewed.DueDate, notice.Candidate.DueDate);
    }

    [Fact]
    public void Plan_SeparatesSubjectsThatShareAnId()
    {
        var id = Guid.CreateVersion7();
        var contract = CandidateDueIn(7) with { Subject = ContractNotificationSubject.Contract, SubjectId = id };
        var warranty = CandidateDueIn(7) with { Subject = ContractNotificationSubject.Warranty, SubjectId = id };
        var sent = Sent(new ContractNotificationKey(
            ContractNotificationSubject.Contract, id, contract.DueDate, 7));

        var notice = Assert.Single(ContractExpiryPlanner.Plan([contract, warranty], Today, sent));

        Assert.Equal(ContractNotificationSubject.Warranty, notice.Candidate.Subject);
    }

    [Theory]
    [InlineData(9, "expires in 9 days on 2026-08-17.")]
    [InlineData(0, "expires today, 2026-08-08.")]
    [InlineData(-2, "expired on 2026-08-06.")]
    public void Message_ReadsAsASentenceAboutTheDueDate(int daysRemaining, string expectedEnding)
    {
        var notice = Assert.Single(ContractExpiryPlanner.Plan([CandidateDueIn(daysRemaining)], Today, Sent()));

        Assert.StartsWith("Support contract C-1 ", notice.Message, StringComparison.Ordinal);
        Assert.EndsWith(expectedEnding, notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_MatchesTheThresholdsTheJobUses()
    {
        Assert.Equal(ContractExpiryStatus.Active, ContractExpiryCalculator.Status(Today.AddDays(31), Today));
        Assert.Equal(ContractExpiryStatus.ExpiringSoon, ContractExpiryCalculator.Status(Today.AddDays(30), Today));
        Assert.Equal(ContractExpiryStatus.ExpiringSoon, ContractExpiryCalculator.Status(Today, Today));
        Assert.Equal(ContractExpiryStatus.Expired, ContractExpiryCalculator.Status(Today.AddDays(-1), Today));
    }

    private static ContractExpiryCandidate CandidateDueIn(int days) => new(
        ContractNotificationSubject.Contract,
        Guid.CreateVersion7(),
        "Support contract C-1",
        Today.AddDays(days),
        "assets@example.test");

    private static IReadOnlySet<ContractNotificationKey> Sent(params ContractNotificationKey[] keys) =>
        new HashSet<ContractNotificationKey>(keys);
}
