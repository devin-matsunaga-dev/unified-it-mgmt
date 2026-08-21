using System.Globalization;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Contracts;

/// <summary>Where a dated agreement or warranty sits relative to today.</summary>
public enum ContractExpiryStatus
{
    Active = 1,
    ExpiringSoon = 2,
    Expired = 3,
}

/// <summary>
/// The one place a due date turns into a status. Contracts, CI warranties and the notification job
/// all read it, so "expiring soon" means the same 30 days everywhere.
/// </summary>
public static class ContractExpiryCalculator
{
    /// <summary>The notice thresholds, tightest last: 30 and 7 days out, then the expiry itself.</summary>
    public static readonly IReadOnlyList<int> Thresholds = [30, 7, 0];

    public static int DaysRemaining(DateOnly dueDate, DateOnly today) => dueDate.DayNumber - today.DayNumber;

    public static ContractExpiryStatus Status(DateOnly dueDate, DateOnly today) => DaysRemaining(dueDate, today) switch
    {
        < 0 => ContractExpiryStatus.Expired,
        <= 30 => ContractExpiryStatus.ExpiringSoon,
        _ => ContractExpiryStatus.Active,
    };

    public static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);
}

/// <summary>Something with an end date that someone should hear about: a contract, or a CI's warranty.</summary>
/// <param name="Recipient">
/// Where the notice goes. One or more addresses joined by <c>;</c>, so the recorded row says who was
/// actually written to rather than only the first of them.
/// </param>
/// <param name="Details">
/// The handful of facts somebody needs to act on this without opening the platform — vendor, owner,
/// cost. Optional: a warranty notice names the asset in <paramref name="SubjectName"/> and has nothing
/// further worth saying.
/// </param>
public sealed record ContractExpiryCandidate(
    ContractNotificationSubject Subject,
    Guid SubjectId,
    string SubjectName,
    DateOnly DueDate,
    string Recipient,
    string? Details = null);

/// <summary>The identity of a notice already raised — the job's dedupe key, mirroring the unique index.</summary>
public readonly record struct ContractNotificationKey(
    ContractNotificationSubject Subject,
    Guid SubjectId,
    DateOnly DueDate,
    int ThresholdDays);

public sealed record ContractExpiryNotice(
    ContractExpiryCandidate Candidate,
    int ThresholdDays,
    int DaysRemaining)
{
    public ContractNotificationKey Key =>
        new(Candidate.Subject, Candidate.SubjectId, Candidate.DueDate, ThresholdDays);

    /// <summary>
    /// What lands in the mail. The first line is the deadline, the second the facts needed to act on
    /// it — kept to one line each, because a renewal notice is read on a phone between jobs.
    /// </summary>
    public string Message => string.IsNullOrWhiteSpace(Candidate.Details)
        ? Headline
        : $"{Headline}\n{Candidate.Details}";

    private string Headline => DaysRemaining switch
    {
        > 0 => $"{Candidate.SubjectName} expires in {DaysRemaining} days on {Date}.",
        0 => $"{Candidate.SubjectName} expires today, {Date}.",
        _ => $"{Candidate.SubjectName} expired on {Date}.",
    };

    private string Date => Candidate.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Decides what to raise on one run. Pure so the whole 30/7/0 rule is testable without a clock.
/// <para>
/// A candidate is only ever notified at the tightest threshold it has crossed, so a job that has not
/// run for a month sends one notice rather than three, and a daily run sends exactly three over the
/// life of a due date. A notice already recorded for the same (subject, due date, threshold) is
/// silent — moving the due date is what starts a new cycle.
/// </para>
/// </summary>
public static class ContractExpiryPlanner
{
    /// <param name="thresholds">
    /// Days before expiry at which a notice is due, from the administrator's settings. Passed in
    /// rather than read from a constant so the rule has one home — the planner decides which
    /// threshold a due date has crossed, and where the numbers come from is not its business.
    /// </param>
    public static IReadOnlyList<ContractExpiryNotice> Plan(
        IEnumerable<ContractExpiryCandidate> candidates,
        DateOnly today,
        IReadOnlySet<ContractNotificationKey> alreadySent,
        IReadOnlyList<int>? thresholds = null)
    {
        var due = thresholds ?? ContractExpiryCalculator.Thresholds;
        var notices = new List<ContractExpiryNotice>();
        foreach (var candidate in candidates)
        {
            var daysRemaining = ContractExpiryCalculator.DaysRemaining(candidate.DueDate, today);
            var threshold = due
                .Where(days => daysRemaining <= days)
                .Cast<int?>()
                .Min();
            if (threshold is not { } crossed)
            {
                continue;
            }

            var notice = new ContractExpiryNotice(candidate, crossed, daysRemaining);
            if (!alreadySent.Contains(notice.Key))
            {
                notices.Add(notice);
            }
        }

        return notices;
    }
}
