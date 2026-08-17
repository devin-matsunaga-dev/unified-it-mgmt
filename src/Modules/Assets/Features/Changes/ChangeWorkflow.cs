using Modules.Assets.Data;

namespace Modules.Assets.Features.Changes;

/// <summary>Why a requested state change was refused, or <see cref="Allowed"/>.</summary>
public enum ChangeTransitionVerdict
{
    Allowed,

    /// <summary>The change is already in that state.</summary>
    NoChange,

    /// <summary>Not a move this workflow makes.</summary>
    NotPermitted,

    /// <summary>A change has to name at least one CI before anybody can agree to it.</summary>
    NeedsCis,

    /// <summary>Its agreed window has already ended, so approving it would open a window in the past.</summary>
    WindowHasPassed,

    /// <summary>Nobody may approve their own change.</summary>
    NeedsASecondPerson,
}

/// <summary>
/// The change lifecycle, as a table rather than as a chain of <c>if</c>s in the service — WP-1.2's ticket
/// workflow and WP-5.7's problem workflow, for the third time and the same reason: a state machine
/// somebody can read is a state machine somebody can argue with.
/// <para>
/// It is deliberately small. Full change management — approval boards, freeze windows, rollback plans —
/// is WORK_PACKAGES 7.A, and half of one invented here is a shape that package would have to undo. What
/// this workflow does carry is the three conditions that make an approval mean something: a change covers
/// something, its window has not already passed, and somebody other than its author agreed to it.
/// </para>
/// </summary>
public static class ChangeWorkflow
{
    /// <summary>
    /// Where each state can go. Approved, Rejected and Cancelled are terminal, and Approved is terminal
    /// for a reason worth stating: the approval has already left the module as an event and a maintenance
    /// window exists because of it. A state change that unpicked that would have to be a second event and
    /// a second decision, which is a work package rather than an arrow.
    /// </summary>
    private static readonly Dictionary<ChangeRequestStatus, ChangeRequestStatus[]> Allowed = new()
    {
        [ChangeRequestStatus.Draft] = [ChangeRequestStatus.Submitted, ChangeRequestStatus.Cancelled],
        [ChangeRequestStatus.Submitted] =
        [
            ChangeRequestStatus.Approved,
            ChangeRequestStatus.Rejected,
            // Back to Draft, which is the only backwards arrow here and it earns its place: a change is
            // editable only while it is a draft, and a window that slips past its planned end while
            // waiting for a decision would otherwise be a change nobody can approve and nobody can fix.
            ChangeRequestStatus.Draft,
            ChangeRequestStatus.Cancelled,
        ],
        [ChangeRequestStatus.Approved] = [],
        [ChangeRequestStatus.Rejected] = [],
        [ChangeRequestStatus.Cancelled] = [],
    };

    public static IReadOnlyList<ChangeRequestStatus> NextFrom(ChangeRequestStatus status) =>
        Allowed.TryGetValue(status, out var targets) ? targets : [];

    /// <param name="ciCount">How many CIs the change covers as it stands.</param>
    /// <param name="actorId">
    /// Who is asking. Compared with the requester so that nobody approves their own change — the one
    /// separation this workflow enforces, and it is enforced here rather than by a policy because it is a
    /// question about a record and not about a role.
    /// </param>
    /// <param name="now">Passed in rather than read, so the window-has-passed rule is testable.</param>
    public static ChangeTransitionVerdict Check(
        ChangeRequest request,
        ChangeRequestStatus target,
        int ciCount,
        string? actorId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Status == target)
        {
            return ChangeTransitionVerdict.NoChange;
        }

        if (!NextFrom(request.Status).Contains(target))
        {
            return ChangeTransitionVerdict.NotPermitted;
        }

        // Submitting and approving both need something to be true of; cancelling and rejecting are ways
        // of saying no, and a change nobody will act on need not be well-formed to be dropped.
        if (target is ChangeRequestStatus.Submitted or ChangeRequestStatus.Approved && ciCount == 0)
        {
            return ChangeTransitionVerdict.NeedsCis;
        }

        if (target == ChangeRequestStatus.Approved)
        {
            // The end, not the start: a change approved as the work begins is the normal case, and
            // refusing it because the planned start slipped by ten minutes would make the feature
            // unusable exactly when it is wanted. A window that has wholly passed is different — it
            // would mute nothing and misreport the estate as having been maintained.
            if (request.PlannedEndAt <= now)
            {
                return ChangeTransitionVerdict.WindowHasPassed;
            }

            if (actorId is null || string.Equals(actorId, request.RequestedById, StringComparison.Ordinal))
            {
                return ChangeTransitionVerdict.NeedsASecondPerson;
            }
        }

        return ChangeTransitionVerdict.Allowed;
    }

    /// <summary>The message a refusal carries, which is the only thing the person who tried it will read.</summary>
    public static string Explain(
        ChangeRequestStatus from,
        ChangeRequestStatus target,
        ChangeTransitionVerdict verdict) => verdict switch
    {
        ChangeTransitionVerdict.NoChange => $"This change is already {from}.",
        ChangeTransitionVerdict.NotPermitted => NextFrom(from) is { Count: > 0 } next
            ? $"A change cannot go from {from} to {target}. From {from} it can become {string.Join(", ", next)}."
            : $"A change that is {from} is finished; it cannot become {target}. Raise a new one.",
        ChangeTransitionVerdict.NeedsCis =>
            "Name at least one configuration item this change will disturb — that list is what the "
            + "maintenance window is made of.",
        ChangeTransitionVerdict.WindowHasPassed =>
            "This change's window has already ended. Move its planned times before approving it, or the "
            + "maintenance window it opens would mute nothing.",
        ChangeTransitionVerdict.NeedsASecondPerson =>
            "A change has to be approved by somebody other than the person who raised it.",
        _ => string.Empty,
    };
}
