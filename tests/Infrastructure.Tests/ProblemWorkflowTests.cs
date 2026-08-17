using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Problems;

namespace Infrastructure.Tests;

/// <summary>
/// The problem lifecycle on its own (WP-5.7). The interesting half is the entry condition on
/// <see cref="ProblemStatus.KnownError"/>: it is what makes the known-error database a database rather
/// than a checkbox, because every row somebody finds in it answers the question they arrived with.
/// </summary>
public sealed class ProblemWorkflowTests
{
    [Fact]
    public void Check_InvestigatingToKnownError_WithACauseAndAWorkaround_IsAllowed() =>
        Assert.Equal(
            ProblemTransitionVerdict.Allowed,
            ProblemWorkflow.Check(
                Problem(ProblemStatus.Investigating, cause: "A failing uplink SFP", workaround: "Move to port 24"),
                ProblemStatus.KnownError,
                resolution: null));

    /// <summary>The failure path this state exists for: a known error with nothing to say.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("A failing uplink SFP", null)]
    [InlineData(null, "Move to port 24")]
    [InlineData("A failing uplink SFP", "   ")]
    public void Check_ToKnownError_WithoutBothHalves_IsRefused(string? cause, string? workaround) =>
        Assert.Equal(
            ProblemTransitionVerdict.NeedsCauseAndWorkaround,
            ProblemWorkflow.Check(
                Problem(ProblemStatus.Investigating, cause, workaround),
                ProblemStatus.KnownError,
                resolution: null));

    [Theory]
    [InlineData(ProblemStatus.Resolved)]
    [InlineData(ProblemStatus.Closed)]
    public void Check_ToAnEnding_WithoutSayingWhatWasDone_IsRefused(ProblemStatus target) =>
        Assert.Equal(
            ProblemTransitionVerdict.NeedsResolution,
            ProblemWorkflow.Check(Problem(ProblemStatus.Investigating), target, resolution: "  "));

    [Theory]
    [InlineData(ProblemStatus.Resolved)]
    [InlineData(ProblemStatus.Closed)]
    public void Check_ToAnEnding_WithAResolution_IsAllowed(ProblemStatus target) =>
        Assert.Equal(
            ProblemTransitionVerdict.Allowed,
            ProblemWorkflow.Check(Problem(ProblemStatus.Investigating), target, resolution: "Replaced the SFP."));

    /// <summary>
    /// A problem resolved in March and closed in April must not be made to retype its own resolution —
    /// the check is against the resolution as it will be, not against what this one request carried.
    /// </summary>
    [Fact]
    public void Check_ResolvedToClosed_WithTheResolutionAlreadyRecorded_IsAllowed()
    {
        var problem = Problem(ProblemStatus.Resolved);
        problem.Resolution = "Replaced the SFP.";

        Assert.Equal(
            ProblemTransitionVerdict.Allowed,
            ProblemWorkflow.Check(problem, ProblemStatus.Closed, problem.Resolution));
    }

    /// <summary>
    /// Every state can be reopened. "We were wrong about the cause" happens, and the alternative is a
    /// second problem about the same fault.
    /// </summary>
    [Theory]
    [InlineData(ProblemStatus.KnownError)]
    [InlineData(ProblemStatus.Resolved)]
    [InlineData(ProblemStatus.Closed)]
    public void Check_BackToInvestigating_IsAllowedFromEverywhere(ProblemStatus from) =>
        Assert.Equal(
            ProblemTransitionVerdict.Allowed,
            ProblemWorkflow.Check(Problem(from), ProblemStatus.Investigating, resolution: null));

    /// <summary>A closed problem is not a known error, however much cause and workaround it carries.</summary>
    [Fact]
    public void Check_ClosedToKnownError_IsNotAMoveThisWorkflowMakes()
    {
        var verdict = ProblemWorkflow.Check(
            Problem(ProblemStatus.Closed, cause: "A failing uplink SFP", workaround: "Move to port 24"),
            ProblemStatus.KnownError,
            resolution: "Replaced the SFP.");

        Assert.Equal(ProblemTransitionVerdict.NotPermitted, verdict);
        Assert.Contains(
            "cannot go from Closed to KnownError",
            ProblemWorkflow.Explain(ProblemStatus.Closed, ProblemStatus.KnownError, verdict),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ToTheStateItIsAlreadyIn_IsNoChange() =>
        Assert.Equal(
            ProblemTransitionVerdict.NoChange,
            ProblemWorkflow.Check(Problem(ProblemStatus.KnownError), ProblemStatus.KnownError, resolution: null));

    /// <summary>The refusal has to name what is possible instead, because it is the only thing anybody reads.</summary>
    [Fact]
    public void Explain_ARefusedMove_NamesWhereTheProblemCanGo()
    {
        var message = ProblemWorkflow.Explain(
            ProblemStatus.Closed, ProblemStatus.Resolved, ProblemTransitionVerdict.NotPermitted);

        Assert.Contains("Investigating", message, StringComparison.Ordinal);
    }

    [Fact]
    public void NextFrom_EveryState_IsNonEmptyAndNeverNamesItself()
    {
        foreach (var status in Enum.GetValues<ProblemStatus>())
        {
            var next = ProblemWorkflow.NextFrom(status);
            Assert.NotEmpty(next);
            Assert.DoesNotContain(status, next);
        }
    }

    private static Problem Problem(ProblemStatus status, string? cause = null, string? workaround = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "Recurring drops on the second floor switch",
        Description = "Five incidents in a week.",
        Status = status,
        Priority = TicketPriority.High,
        RootCause = cause,
        Workaround = workaround,
    };
}
