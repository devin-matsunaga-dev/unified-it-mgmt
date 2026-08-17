using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Problems;

/// <summary>A subject and how many incidents landed on it inside the window.</summary>
public sealed record RecurrenceCandidate(
    ProblemSuggestionScope Scope,
    Guid SubjectId,
    int IncidentCount,
    DateTimeOffset FirstIncidentAt,
    DateTimeOffset LastIncidentAt);

/// <summary>
/// What is already known about a subject: whether somebody is working it, whether a suggestion about it
/// is already waiting, and when one was last dismissed.
/// </summary>
public sealed record RecurrenceSubjectState(
    bool HasOpenProblem = false,
    bool HasOpenSuggestion = false,
    DateTimeOffset? DismissedAt = null);

/// <summary>Why a candidate did or did not become a suggestion. Every candidate gets one.</summary>
public enum RecurrenceDecision
{
    /// <summary>Raise it.</summary>
    Suggest,

    /// <summary>Fewer incidents than the threshold. Most candidates, every night.</summary>
    BelowThreshold,

    /// <summary>Somebody is already working a problem about this subject.</summary>
    AlreadyAProblem,

    /// <summary>A suggestion about this subject is already waiting to be looked at.</summary>
    AlreadySuggested,

    /// <summary>Somebody dismissed a suggestion about this subject recently enough that asking again would be nagging.</summary>
    DismissalStillHolds,

    /// <summary>Real, but beyond the number of suggestions one pass is allowed to raise.</summary>
    OverRunLimit,
}

public sealed record RecurrenceVerdict(RecurrenceCandidate Candidate, RecurrenceDecision Decision);

/// <summary>
/// The whole of "is this a recurrence worth telling somebody about", with no database in it.
/// <para>
/// Separated from the service for the reason <c>ContractExpiryPlanner</c> and <c>ImpactAnalyzer</c> are:
/// the counting is a query and the judgement is a rule, and only one of the two is worth testing against
/// a hundred combinations. Every candidate comes back with a verdict rather than only the survivors, so
/// the pass can say why it stayed quiet — which is the question somebody asks when a switch has failed
/// six times and no suggestion appeared.
/// </para>
/// </summary>
public static class RecurrenceDetector
{
    /// <summary>
    /// Judges each candidate against the options and what is already known about its subject.
    /// </summary>
    /// <param name="states">
    /// Keyed by scope and subject id. A subject absent from the map is one nothing is known about, which
    /// is the common case and the one that suggests.
    /// </param>
    /// <returns>
    /// One verdict per candidate, ordered as the candidates were with the suggestions decided
    /// highest-count-first — so that when the per-run limit bites it takes the smallest recurrences
    /// rather than whichever the database happened to return last.
    /// </returns>
    public static IReadOnlyList<RecurrenceVerdict> Decide(
        IReadOnlyCollection<RecurrenceCandidate> candidates,
        IReadOnlyDictionary<(ProblemSuggestionScope Scope, Guid SubjectId), RecurrenceSubjectState> states,
        ProblemDetectionOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(options);

        var cooldown = TimeSpan.FromDays(options.DismissalCooldownDays);
        // Keyed by what identifies a subject rather than by the candidate record, whose value equality
        // would quietly merge two candidates that happened to carry the same counts.
        var verdicts = new Dictionary<(ProblemSuggestionScope, Guid), RecurrenceDecision>();
        var raised = 0;

        // Worst first, so the run limit is a cap on how much is said rather than a lottery over which
        // recurrences get said at all.
        foreach (var candidate in candidates
            .OrderByDescending(candidate => candidate.IncidentCount)
            .ThenBy(candidate => candidate.SubjectId))
        {
            states.TryGetValue((candidate.Scope, candidate.SubjectId), out var state);
            state ??= new RecurrenceSubjectState();

            var decision = Judge(candidate, state, options, cooldown, now);
            if (decision == RecurrenceDecision.Suggest && raised >= options.MaxSuggestionsPerRun)
            {
                decision = RecurrenceDecision.OverRunLimit;
            }
            else if (decision == RecurrenceDecision.Suggest)
            {
                raised++;
            }

            verdicts[(candidate.Scope, candidate.SubjectId)] = decision;
        }

        return [.. candidates.Select(candidate =>
            new RecurrenceVerdict(candidate, verdicts[(candidate.Scope, candidate.SubjectId)]))];
    }

    private static RecurrenceDecision Judge(
        RecurrenceCandidate candidate,
        RecurrenceSubjectState state,
        ProblemDetectionOptions options,
        TimeSpan cooldown,
        DateTimeOffset now)
    {
        // Cheapest and by far the most common answer, so it goes first: nearly everything the pass counts
        // is one or two incidents on something that is simply in use.
        if (candidate.IncidentCount < options.MinimumIncidents)
        {
            return RecurrenceDecision.BelowThreshold;
        }

        // The rule that stops the inbox restating what somebody is already doing. Note that it is about an
        // *open* problem: a switch whose problem was closed last month and has started failing again is a
        // new recurrence and deserves to be raised as one.
        if (state.HasOpenProblem)
        {
            return RecurrenceDecision.AlreadyAProblem;
        }

        if (state.HasOpenSuggestion)
        {
            return RecurrenceDecision.AlreadySuggested;
        }

        // A dismissal has to mean something, or dismissing is a button that does nothing until tomorrow.
        // It is a cooldown rather than a permanent silence because a recurrence nobody fixed is still a
        // recurrence, and the person who dismissed it in March is not necessarily still watching in June.
        if (state.DismissedAt is { } dismissedAt && now - dismissedAt < cooldown)
        {
            return RecurrenceDecision.DismissalStillHolds;
        }

        return RecurrenceDecision.Suggest;
    }
}
