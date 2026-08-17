using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Problems;

/// <summary>Why a requested state change was refused, or <see cref="Allowed"/>.</summary>
public enum ProblemTransitionVerdict
{
    Allowed,

    /// <summary>The problem is already in that state.</summary>
    NoChange,

    /// <summary>Not a move this workflow makes.</summary>
    NotPermitted,

    /// <summary>A known error has to say what is wrong and what to do about it. One or both are missing.</summary>
    NeedsCauseAndWorkaround,

    /// <summary>Resolving or closing has to say what was done, following the ticket workflow's resolution note.</summary>
    NeedsResolution,
}

/// <summary>
/// The problem lifecycle, as a table rather than as a chain of <c>if</c>s in the service.
/// <para>
/// Modelled on WP-1.2's ticket workflow — the same reasoning applies, that a state machine somebody can
/// read is a state machine somebody can argue with. It differs from the ticket's in one way that matters:
/// entry into <see cref="ProblemStatus.KnownError"/> has a <em>condition</em> and not just a source. That
/// condition is what makes the known-error database a database: a problem is in it only once it carries a
/// root cause and a workaround, so every row somebody finds there answers the question they came with.
/// </para>
/// </summary>
public static class ProblemWorkflow
{
    /// <summary>
    /// Where each state can go. Every state can be reopened to <see cref="ProblemStatus.Investigating"/>,
    /// because "we were wrong about the cause" is a thing that happens and the alternative is a second
    /// problem about the same fault.
    /// </summary>
    private static readonly Dictionary<ProblemStatus, ProblemStatus[]> Allowed = new()
    {
        [ProblemStatus.Investigating] = [ProblemStatus.KnownError, ProblemStatus.Resolved, ProblemStatus.Closed],
        [ProblemStatus.KnownError] = [ProblemStatus.Investigating, ProblemStatus.Resolved, ProblemStatus.Closed],
        [ProblemStatus.Resolved] = [ProblemStatus.Closed, ProblemStatus.Investigating],
        [ProblemStatus.Closed] = [ProblemStatus.Investigating],
    };

    public static IReadOnlyList<ProblemStatus> NextFrom(ProblemStatus status) =>
        Allowed.TryGetValue(status, out var targets) ? targets : [];

    /// <param name="resolution">
    /// The resolution as it will be after the write — the request's, or what the problem already carries
    /// when the request leaves it alone. A problem resolved in March and closed in April must not be made
    /// to retype its own resolution.
    /// </param>
    public static ProblemTransitionVerdict Check(
        Problem problem,
        ProblemStatus target,
        string? resolution)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Status == target)
        {
            return ProblemTransitionVerdict.NoChange;
        }

        if (!NextFrom(problem.Status).Contains(target))
        {
            return ProblemTransitionVerdict.NotPermitted;
        }

        if (target == ProblemStatus.KnownError
            && (string.IsNullOrWhiteSpace(problem.RootCause) || string.IsNullOrWhiteSpace(problem.Workaround)))
        {
            return ProblemTransitionVerdict.NeedsCauseAndWorkaround;
        }

        if (target is ProblemStatus.Resolved or ProblemStatus.Closed && string.IsNullOrWhiteSpace(resolution))
        {
            return ProblemTransitionVerdict.NeedsResolution;
        }

        return ProblemTransitionVerdict.Allowed;
    }

    /// <summary>The message a refusal carries, which is the only thing the person who tried it will read.</summary>
    public static string Explain(ProblemStatus from, ProblemStatus target, ProblemTransitionVerdict verdict) => verdict switch
    {
        ProblemTransitionVerdict.NoChange => $"This problem is already {from}.",
        ProblemTransitionVerdict.NotPermitted =>
            $"A problem cannot go from {from} to {target}. From {from} it can become "
            + $"{string.Join(", ", NextFrom(from))}.",
        ProblemTransitionVerdict.NeedsCauseAndWorkaround =>
            "A known error has to record both a root cause and a workaround — that is what makes it findable "
            + "and useful to somebody holding a fresh incident.",
        ProblemTransitionVerdict.NeedsResolution =>
            $"Say what was done before marking a problem {target}.",
        _ => string.Empty,
    };
}
