using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Platform.Data;

namespace Platform.Notifications;

/// <param name="Groups">How many roll-up messages were sent (or attempted).</param>
/// <param name="Notifications">How many withheld notifications those roll-ups covered.</param>
/// <param name="Failed">Roll-ups the channel refused. Their contributors stay withheld for the next pass.</param>
public readonly record struct NotificationDigestReport(int Groups, int Notifications, int Failed);

public interface INotificationDigestService
{
    /// <summary>
    /// Send one roll-up per destination for everything quiet hours withheld and that is now due.
    /// Safe to run at any time and as often as you like: a pass with nothing due does nothing.
    /// </summary>
    Task<NotificationDigestReport> RunAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class NotificationDigestService(
    PlatformDbContext dbContext,
    IEnumerable<INotificationChannel> channels,
    ILogger<NotificationDigestService> logger) : INotificationDigestService
{
    /// <summary>One pass will not try to summarise an unbounded backlog; what is left waits for the next.</summary>
    public const int MaximumPerPass = 500;

    /// <summary>Enough for the digest to be a summary. Beyond it, the count is the message.</summary>
    public const int MaximumListedLines = 20;

    public async Task<NotificationDigestReport> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await dbContext.NotificationDeliveries
            .Where(delivery => delivery.Outcome == NotificationDeliveryOutcome.Deferred
                && delivery.ReleaseAfter != null
                && delivery.ReleaseAfter <= now)
            .OrderBy(delivery => delivery.ReleaseAfter)
            .Take(MaximumPerPass)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return new(0, 0, 0);
        }

        var groups = due
            .GroupBy(delivery => (delivery.ChannelId, delivery.UserId))
            .ToList();

        var sentGroups = 0;
        var sentNotifications = 0;
        var failed = 0;
        foreach (var group in groups)
        {
            var items = group.OrderBy(delivery => delivery.OccurredAt).ToList();
            var (target, unavailable) = await ResolveTargetAsync(group.Key.ChannelId, group.Key.UserId, cancellationToken);
            var kind = items[0].ChannelKind;
            var envelope = BuildDigest(items);

            NotificationDispatchResult result;
            if (target is null)
            {
                result = NotificationDispatchResult.Failure(unavailable ?? "The destination no longer exists.");
            }
            else if (channels.FirstOrDefault(channel => channel.Kind == kind) is not { } sender)
            {
                result = NotificationDispatchResult.Failure($"No channel is registered for {kind}.");
            }
            else
            {
                result = await sender.SendAsync(target, envelope, cancellationToken);
            }

            if (!result.Delivered)
            {
                // The contributors stay Deferred, so the next pass tries again. A duplicate digest is a
                // better failure than a silent hole, which is the same trade every retry in the
                // platform makes.
                failed++;
                logger.LogWarning(
                    "Notification digest for channel {ChannelId} / user {UserId} failed and will be retried: {Detail}",
                    group.Key.ChannelId, group.Key.UserId, result.Detail);
                continue;
            }

            var digest = new NotificationDelivery
            {
                Id = Guid.CreateVersion7(),
                EventKind = DigestEventKind,
                Severity = items.Max(item => item.Severity),
                Subject = envelope.Subject,
                Body = EmailNotificationChannel.RenderBody(envelope),
                ChannelId = group.Key.ChannelId,
                ChannelKind = kind,
                TargetRedacted = NotificationChannel.Redact(kind, target!),
                UserId = group.Key.UserId,
                Outcome = NotificationDeliveryOutcome.Sent,
                DigestOfCount = items.Count,
                OccurredAt = now,
                CompletedAt = now,
            };
            dbContext.NotificationDeliveries.Add(digest);

            foreach (var item in items)
            {
                item.Outcome = NotificationDeliveryOutcome.Digested;
                item.DigestDeliveryId = digest.Id;
                item.CompletedAt = now;
            }

            sentGroups++;
            sentNotifications += items.Count;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new(sentGroups, sentNotifications, failed);
    }

    public const string DigestEventKind = "NotificationDigest";

    /// <summary>
    /// One message stating what was withheld and, where several notifications concerned the same
    /// problem, how many times — the point of a digest is that an alert that flapped eleven times
    /// overnight reads as one line with an eleven on it.
    /// </summary>
    public static NotificationEnvelope BuildDigest(IReadOnlyList<NotificationDelivery> items)
    {
        var worst = items.Max(item => item.Severity);
        var window = items.Min(item => item.OccurredAt);
        var lines = items
            .GroupBy(item => item.DedupeKey ?? item.Subject, StringComparer.Ordinal)
            .Select(group => new
            {
                group.First().Subject,
                group.First().Severity,
                Count = group.Count(),
                Latest = group.Max(item => item.OccurredAt),
            })
            .OrderByDescending(line => line.Severity)
            .ThenByDescending(line => line.Latest)
            .ToList();

        var body = new List<string>
        {
            $"{items.Count} notification(s) were held during quiet hours, the earliest at {window:u}.",
            string.Empty,
        };
        body.AddRange(lines.Take(MaximumListedLines).Select(line =>
            line.Count > 1
                ? $"[{line.Severity}] {StripSeverityPrefix(line.Subject)} (×{line.Count})"
                : $"[{line.Severity}] {StripSeverityPrefix(line.Subject)}"));
        if (lines.Count > MaximumListedLines)
        {
            body.Add($"…and {lines.Count - MaximumListedLines} more.");
        }

        return new NotificationEnvelope(
            DigestEventKind,
            worst,
            $"Quiet hours digest: {items.Count} notification(s)",
            string.Join(Environment.NewLine, body),
            // The single deep link a digest can honestly carry is the one belonging to its worst item;
            // a roll-up of many problems has no one page of its own.
            DeepLink: lines.Count == 1 ? items[0].DeepLink : null,
            Facts:
            [
                new NotificationFact("Held", items.Count.ToString()),
                new NotificationFact("Distinct", lines.Count.ToString()),
                new NotificationFact("Worst severity", worst.ToString()),
            ]);
    }

    /// <summary>
    /// A digest line states its own severity, so a subject that already opens with one would read
    /// "[Critical] [Critical] …". Alert subjects carry the prefix because it is worth having in an
    /// email subject line and a chat title; SLA subjects do not, which is why the digest adds its own
    /// rather than relying on the subject. Only a leading bracketed word is removed — a subject that
    /// merely contains brackets is left alone.
    /// </summary>
    public static string StripSeverityPrefix(string subject)
    {
        if (subject.Length == 0 || subject[0] != '[')
        {
            return subject;
        }

        var close = subject.IndexOf(']', StringComparison.Ordinal);
        return close < 0 ? subject : subject[(close + 1)..].TrimStart();
    }

    /// <summary>
    /// The destination is re-resolved at release time rather than read off the withheld row, which
    /// only ever stored a redacted form. A channel deleted while its notifications were held has no
    /// destination any more, and that is reported rather than guessed at.
    /// </summary>
    private async Task<(string? Target, string? Reason)> ResolveTargetAsync(
        Guid? channelId,
        string? userId,
        CancellationToken cancellationToken)
    {
        if (channelId is { } id)
        {
            var channel = await dbContext.NotificationChannels.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            return channel is null
                ? (null, "The channel has been deleted since the notification was held.")
                : channel.IsActive
                    ? (channel.Target, null)
                    : (null, "The channel has been deactivated since the notification was held.");
        }

        if (userId is null)
        {
            return (null, "The notification names neither a channel nor a recipient.");
        }

        var preference = await dbContext.UserNotificationPreferences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (preference?.EmailAddress is { Length: > 0 } configured)
        {
            return (configured, null);
        }

        if (userId.Contains('@', StringComparison.Ordinal))
        {
            return (userId, null);
        }

        var byId = Guid.TryParse(userId, out var parsed) ? parsed : (Guid?)null;
        var address = await dbContext.UserProfiles.AsNoTracking()
            .Where(user => user.Username == userId || (byId != null && user.Id == byId))
            .Select(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);
        return address is null
            ? (null, "No email address is recorded for this recipient.")
            : (address, null);
    }
}
