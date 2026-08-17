using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Problems;

namespace Infrastructure.Tests;

/// <summary>
/// The recurrence rules on their own, with no database (WP-5.7): when a run of incidents is worth telling
/// somebody about, and — more importantly — the four reasons it is not.
/// <para>
/// The silences are what these tests are really for. A detector that suggests too readily produces an
/// inbox nobody opens, and every one of the reasons below is a case where the platform already knows the
/// answer would not help: somebody is working it, somebody has been told, or somebody has said no.
/// </para>
/// </summary>
public sealed class RecurrenceDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 2, 0, 0, TimeSpan.Zero);
    private static readonly Guid Switch = Guid.Parse("01990000-0000-7000-8000-000000000001");
    private static readonly Guid Laptops = Guid.Parse("01990000-0000-7000-8000-000000000002");

    /// <summary>The WP's own verification: five incidents on one switch is a suggestion.</summary>
    [Fact]
    public void Decide_ForFiveIncidentsOnOneCi_Suggests()
    {
        var verdict = Assert.Single(Decide([Candidate(ProblemSuggestionScope.Ci, Switch, 5)]));

        Assert.Equal(RecurrenceDecision.Suggest, verdict.Decision);
        Assert.Equal(Switch, verdict.Candidate.SubjectId);
    }

    /// <summary>The threshold is "at least", not "more than" — the WP says ≥N.</summary>
    [Fact]
    public void Decide_ForExactlyTheThreshold_Suggests() =>
        Assert.Equal(
            RecurrenceDecision.Suggest,
            Assert.Single(Decide([Candidate(ProblemSuggestionScope.Ci, Switch, 5)], options: Options(minimum: 5))).Decision);

    [Fact]
    public void Decide_ForOneShortOfTheThreshold_StaysQuiet() =>
        Assert.Equal(
            RecurrenceDecision.BelowThreshold,
            Assert.Single(Decide([Candidate(ProblemSuggestionScope.Ci, Switch, 4)], options: Options(minimum: 5))).Decision);

    /// <summary>A category recurrence is the same rule against a different grouping.</summary>
    [Fact]
    public void Decide_ForACategoryOverTheThreshold_Suggests() =>
        Assert.Equal(
            RecurrenceDecision.Suggest,
            Assert.Single(Decide([Candidate(ProblemSuggestionScope.Category, Laptops, 9)])).Decision);

    /// <summary>
    /// The rule that stops the inbox restating what somebody is already doing.
    /// </summary>
    [Fact]
    public void Decide_ForASubjectWithAnOpenProblem_StaysQuiet() =>
        Assert.Equal(
            RecurrenceDecision.AlreadyAProblem,
            Assert.Single(Decide(
                [Candidate(ProblemSuggestionScope.Ci, Switch, 12)],
                states: State(ProblemSuggestionScope.Ci, Switch, new RecurrenceSubjectState(HasOpenProblem: true))))
                .Decision);

    /// <summary>
    /// A problem that has been closed does not silence the subject forever. A switch whose problem was
    /// closed last month and has started failing again is a new recurrence, and the whole value of the
    /// nightly pass is that it says so.
    /// </summary>
    [Fact]
    public void Decide_ForASubjectWhoseProblemIsClosed_SuggestsAgain() =>
        Assert.Equal(
            RecurrenceDecision.Suggest,
            Assert.Single(Decide(
                [Candidate(ProblemSuggestionScope.Ci, Switch, 6)],
                states: State(ProblemSuggestionScope.Ci, Switch, new RecurrenceSubjectState(HasOpenProblem: false))))
                .Decision);

    /// <summary>Idempotence, which is what lets the pass run at start-up and be pressed by hand.</summary>
    [Fact]
    public void Decide_ForASubjectAlreadySuggested_StaysQuiet() =>
        Assert.Equal(
            RecurrenceDecision.AlreadySuggested,
            Assert.Single(Decide(
                [Candidate(ProblemSuggestionScope.Ci, Switch, 8)],
                states: State(ProblemSuggestionScope.Ci, Switch, new RecurrenceSubjectState(HasOpenSuggestion: true))))
                .Decision);

    /// <summary>A dismissal has to mean something, or dismissing is a button that works until tomorrow.</summary>
    [Fact]
    public void Decide_ForASubjectDismissedInsideTheCooldown_StaysQuiet() =>
        Assert.Equal(
            RecurrenceDecision.DismissalStillHolds,
            Assert.Single(Decide(
                [Candidate(ProblemSuggestionScope.Ci, Switch, 8)],
                states: State(ProblemSuggestionScope.Ci, Switch,
                    new RecurrenceSubjectState(DismissedAt: Now.AddDays(-2))),
                options: Options(cooldownDays: 7)))
                .Decision);

    /// <summary>
    /// And it has to stop meaning something eventually: a recurrence nobody fixed is still a recurrence,
    /// and the person who dismissed it in March is not necessarily still watching in June.
    /// </summary>
    [Fact]
    public void Decide_ForASubjectDismissedBeforeTheCooldownExpired_SuggestsAgain() =>
        Assert.Equal(
            RecurrenceDecision.Suggest,
            Assert.Single(Decide(
                [Candidate(ProblemSuggestionScope.Ci, Switch, 8)],
                states: State(ProblemSuggestionScope.Ci, Switch,
                    new RecurrenceSubjectState(DismissedAt: Now.AddDays(-8))),
                options: Options(cooldownDays: 7)))
                .Decision);

    /// <summary>
    /// The order the checks run in is itself a decision: a subject that is already a problem is reported
    /// as such rather than as "already suggested", because the two send somebody to different places.
    /// </summary>
    [Fact]
    public void Decide_ForASubjectThatIsBothAProblemAndSuggested_SaysItIsAProblem() =>
        Assert.Equal(
            RecurrenceDecision.AlreadyAProblem,
            Assert.Single(Decide(
                [Candidate(ProblemSuggestionScope.Ci, Switch, 8)],
                states: State(ProblemSuggestionScope.Ci, Switch,
                    new RecurrenceSubjectState(HasOpenProblem: true, HasOpenSuggestion: true))))
                .Decision);

    /// <summary>
    /// A CI and a category that happen to share an id are two different subjects. They cannot collide in
    /// practice — one comes from the CMDB and the other from this schema — but the detector is keyed on
    /// the pair and it is worth pinning that it is.
    /// </summary>
    [Fact]
    public void Decide_ForACiAndACategorySharingAnId_JudgesThemSeparately()
    {
        var verdicts = Decide(
            [
                Candidate(ProblemSuggestionScope.Ci, Switch, 6),
                Candidate(ProblemSuggestionScope.Category, Switch, 6),
            ],
            states: State(ProblemSuggestionScope.Ci, Switch, new RecurrenceSubjectState(HasOpenProblem: true)));

        Assert.Equal(RecurrenceDecision.AlreadyAProblem,
            Assert.Single(verdicts, verdict => verdict.Candidate.Scope == ProblemSuggestionScope.Ci).Decision);
        Assert.Equal(RecurrenceDecision.Suggest,
            Assert.Single(verdicts, verdict => verdict.Candidate.Scope == ProblemSuggestionScope.Category).Decision);
    }

    /// <summary>
    /// The per-run bound, and the half of it that matters: when it bites it keeps the worst recurrences
    /// and drops the smallest, rather than keeping whichever the database returned first.
    /// </summary>
    [Fact]
    public void Decide_WhenMoreRecurrencesThanTheRunLimit_KeepsTheWorstOnes()
    {
        var candidates = Enumerable.Range(1, 5)
            .Select(index => Candidate(ProblemSuggestionScope.Ci, Guid.Parse($"01990000-0000-7000-8000-00000000000{index}"), index + 5))
            .ToArray();

        var verdicts = Decide(candidates, options: Options(maxPerRun: 2));

        var suggested = verdicts.Where(verdict => verdict.Decision == RecurrenceDecision.Suggest).ToList();
        Assert.Equal(2, suggested.Count);
        // Verdicts come back in the order the candidates went in, so this is a claim about which two
        // survived rather than about the order they are reported in.
        Assert.Equal(
            new[] { 9, 10 },
            suggested.Select(verdict => verdict.Candidate.IncidentCount).Order().ToArray());
        Assert.Equal(3, verdicts.Count(verdict => verdict.Decision == RecurrenceDecision.OverRunLimit));
    }

    /// <summary>Every candidate comes back with a verdict, in the order it went in — the pass logs them all.</summary>
    [Fact]
    public void Decide_ReturnsOneVerdictPerCandidateInTheOrderGiven()
    {
        var candidates = new[]
        {
            Candidate(ProblemSuggestionScope.Ci, Switch, 2),
            Candidate(ProblemSuggestionScope.Category, Laptops, 20),
        };

        var verdicts = Decide(candidates);

        Assert.Equal(2, verdicts.Count);
        Assert.Equal(Switch, verdicts[0].Candidate.SubjectId);
        Assert.Equal(RecurrenceDecision.BelowThreshold, verdicts[0].Decision);
        Assert.Equal(Laptops, verdicts[1].Candidate.SubjectId);
        Assert.Equal(RecurrenceDecision.Suggest, verdicts[1].Decision);
    }

    [Fact]
    public void Decide_WithNothingToJudge_ReturnsNothing() =>
        Assert.Empty(RecurrenceDetector.Decide([], new Dictionary<(ProblemSuggestionScope, Guid), RecurrenceSubjectState>(), Options(), Now));

    private static IReadOnlyList<RecurrenceVerdict> Decide(
        RecurrenceCandidate[] candidates,
        IReadOnlyDictionary<(ProblemSuggestionScope, Guid), RecurrenceSubjectState>? states = null,
        ProblemDetectionOptions? options = null) =>
        RecurrenceDetector.Decide(
            candidates,
            states ?? new Dictionary<(ProblemSuggestionScope, Guid), RecurrenceSubjectState>(),
            options ?? Options(),
            Now);

    private static Dictionary<(ProblemSuggestionScope, Guid), RecurrenceSubjectState> State(
        ProblemSuggestionScope scope,
        Guid subjectId,
        RecurrenceSubjectState state) => new() { [(scope, subjectId)] = state };

    private static RecurrenceCandidate Candidate(ProblemSuggestionScope scope, Guid subjectId, int count) =>
        new(scope, subjectId, count, Now.AddDays(-6), Now.AddHours(-1));

    private static ProblemDetectionOptions Options(int minimum = 5, int cooldownDays = 7, int maxPerRun = 25) =>
        new()
        {
            MinimumIncidents = minimum,
            WindowDays = 7,
            DismissalCooldownDays = cooldownDays,
            MaxSuggestionsPerRun = maxPerRun,
        };
}
