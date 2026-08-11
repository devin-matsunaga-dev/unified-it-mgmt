using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Platform.Data;

namespace Platform.Notifications;

/// <param name="Sent">Deliveries handed to a channel and accepted.</param>
/// <param name="Deferred">Held by quiet hours and waiting for a digest.</param>
/// <param name="Suppressed">Declined outright — a preference that says no, or a schedule that drops.</param>
/// <param name="Failed">Attempted and refused by the channel.</param>
public readonly record struct NotificationRoutingReport(int Sent, int Deferred, int Suppressed, int Failed)
{
    public int Total => Sent + Deferred + Suppressed + Failed;
}

public interface INotificationRouter
{
    /// <summary>
    /// Deliver one notification everywhere the configuration says it should go: every routing rule
    /// that matches, plus every named user's own preference.
    /// <para>
    /// Never throws. A notification is a consequence of something that already happened — an alert
    /// raised, an SLA breached — and a channel that is down must not be able to undo it. Everything
    /// attempted is recorded in <c>platform.notification_deliveries</c>, which is where a run's real
    /// behaviour is read afterwards.
    /// </para>
    /// </summary>
    /// <param name="userIds">
    /// People this concerns personally, if any. Their preference decides whether they hear about it;
    /// a routing rule's channel belongs to a team and is nobody's to mute.
    /// </param>
    Task<NotificationRoutingReport> RouteAsync(
        NotificationEnvelope envelope,
        IReadOnlyCollection<string>? userIds,
        CancellationToken cancellationToken);
}

public sealed class NotificationRouter(
    PlatformDbContext dbContext,
    IEnumerable<INotificationChannel> channels,
    ILogger<NotificationRouter> logger) : INotificationRouter
{
    /// <summary>
    /// What a user with no preference row gets: everything, by email, at any hour. It has to be
    /// permissive — a technician who has never opened the preferences screen must still be told their
    /// SLA is about to breach, which is also the behaviour every package before this one had.
    /// </summary>
    public static UserNotificationPreference DefaultPreference(string userId) => new()
    {
        Id = Guid.Empty,
        UserId = userId,
        EmailEnabled = true,
        MinimumSeverity = NotificationSeverity.Informational,
        TimeZone = "UTC",
        DigestQuietHours = true,
    };

    public async Task<NotificationRoutingReport> RouteAsync(
        NotificationEnvelope envelope,
        IReadOnlyCollection<string>? userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var now = DateTimeOffset.UtcNow;
        var deliveries = new List<NotificationDelivery>();

        try
        {
            foreach (var (rule, channel) in await MatchRulesAsync(envelope, cancellationToken))
            {
                deliveries.Add(await DeliverAsync(
                    envelope, now, channel, channel.Target, QuietHoursSchedule.From(rule),
                    rule.DigestQuietHours, rule.Id, userId: null, cancellationToken));
            }

            foreach (var userId in Normalise(userIds))
            {
                if (await DeliverToUserAsync(envelope, now, userId, cancellationToken) is { } delivery)
                {
                    deliveries.Add(delivery);
                }
            }

            // Recorded after the attempts rather than before them, unlike WP-2.6's expiry notices: the
            // row here states an outcome, and this path is already protected from replay by the
            // consumer's idempotency key rather than by the row's own existence.
            dbContext.NotificationDeliveries.AddRange(deliveries);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Routing {EventKind} ({Severity}) failed after {Count} deliveries; the notification is lost.",
                envelope.EventKind, envelope.Severity, deliveries.Count);
        }

        return new(
            deliveries.Count(delivery => delivery.Outcome is NotificationDeliveryOutcome.Sent),
            deliveries.Count(delivery => delivery.Outcome is NotificationDeliveryOutcome.Deferred),
            deliveries.Count(delivery => delivery.Outcome is NotificationDeliveryOutcome.Suppressed),
            deliveries.Count(delivery => delivery.Outcome is NotificationDeliveryOutcome.Failed));
    }

    /// <summary>
    /// Every active rule whose filters the envelope satisfies, reduced to one rule per channel.
    /// <para>
    /// Rules are additive — "Critical to Teams and email, Warning to email only" is two rules, not an
    /// order somebody has to maintain — but one channel must not receive the same notification twice
    /// because two rules happened to select it. Where they collide, a rule that would send now beats
    /// one that would withhold, and a rule that digests beats one that drops: the collision resolves
    /// toward the operator hearing about it.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<(NotificationRoutingRule Rule, NotificationChannel Channel)>> MatchRulesAsync(
        NotificationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        // Severity is stored as its name, so the ordering comparison cannot be a `where` clause —
        // `>= 'Warning'` in SQL would compare spellings, the same trap WP-3.9 recorded for the alert
        // board's sort. There are a handful of rules; they are filtered in memory.
        var rules = await dbContext.NotificationRoutingRules
            .AsNoTracking()
            .Include(rule => rule.Channel)
            .Where(rule => rule.IsActive && rule.Channel.IsActive)
            .OrderBy(rule => rule.Name)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        return [.. rules
            .Where(rule => Matches(rule, envelope))
            .GroupBy(rule => rule.ChannelId)
            .Select(group => group
                .OrderBy(rule => QuietHoursSchedule.From(rule).Evaluate(now).IsQuiet ? 1 : 0)
                .ThenBy(rule => rule.DigestQuietHours ? 0 : 1)
                .ThenBy(rule => rule.Name, StringComparer.Ordinal)
                .First())
            .Select(rule => (rule, rule.Channel))];
    }

    public static bool Matches(NotificationRoutingRule rule, NotificationEnvelope envelope) =>
        envelope.Severity >= rule.MinimumSeverity
        && (rule.EventKind is null
            || string.Equals(rule.EventKind, envelope.EventKind, StringComparison.OrdinalIgnoreCase))
        // A rule that names a device group is about devices. An envelope carrying no group — an SLA
        // breach, say — is deliberately not "every group": it is not about a device at all.
        && (rule.DeviceGroup is null
            || string.Equals(rule.DeviceGroup, envelope.DeviceGroup, StringComparison.OrdinalIgnoreCase));

    private async Task<NotificationDelivery?> DeliverToUserAsync(
        NotificationEnvelope envelope,
        DateTimeOffset now,
        string userId,
        CancellationToken cancellationToken)
    {
        var preference = await dbContext.UserNotificationPreferences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken)
            ?? DefaultPreference(userId);

        var address = preference.EmailAddress ?? await ResolveAddressAsync(userId, cancellationToken);
        if (!preference.EmailEnabled)
        {
            return Record(envelope, now, null, NotificationChannelKind.Email, address ?? userId, userId,
                null, NotificationDeliveryOutcome.Suppressed, "The recipient has turned email notifications off.");
        }

        if (envelope.Severity < preference.MinimumSeverity)
        {
            return Record(envelope, now, null, NotificationChannelKind.Email, address ?? userId, userId, null,
                NotificationDeliveryOutcome.Suppressed,
                $"Below the recipient's minimum severity of {preference.MinimumSeverity}.");
        }

        if (address is null)
        {
            // Not a failure to deliver — there is nowhere to deliver to. Seeded helpdesk identities are
            // usernames rather than directory ids (WP-1.11), so this is a real and diagnosable case.
            return Record(envelope, now, null, NotificationChannelKind.Email, userId, userId, null,
                NotificationDeliveryOutcome.Suppressed,
                "No email address is recorded for this recipient, in their preferences or the directory.");
        }

        return await DeliverAsync(
            envelope, now, channel: null, address, QuietHoursSchedule.From(preference),
            preference.DigestQuietHours, ruleId: null, userId, cancellationToken);
    }

    private async Task<NotificationDelivery> DeliverAsync(
        NotificationEnvelope envelope,
        DateTimeOffset now,
        NotificationChannel? channel,
        string target,
        QuietHoursSchedule schedule,
        bool digest,
        Guid? ruleId,
        string? userId,
        CancellationToken cancellationToken)
    {
        var kind = channel?.Kind ?? NotificationChannelKind.Email;
        var quiet = schedule.Evaluate(now);
        if (quiet.IsQuiet)
        {
            return digest
                ? Record(envelope, now, channel, kind, target, userId, ruleId,
                    NotificationDeliveryOutcome.Deferred,
                    $"Held until quiet hours end at {quiet.ReleaseAfter:u}.", quiet.ReleaseAfter)
                : Record(envelope, now, channel, kind, target, userId, ruleId,
                    NotificationDeliveryOutcome.Suppressed, "Inside quiet hours, which do not digest.");
        }

        var sender = channels.FirstOrDefault(item => item.Kind == kind);
        if (sender is null)
        {
            return Record(envelope, now, channel, kind, target, userId, ruleId,
                NotificationDeliveryOutcome.Failed, $"No channel is registered for {kind}.");
        }

        var result = await sender.SendAsync(target, envelope, cancellationToken);
        return Record(envelope, now, channel, kind, target, userId, ruleId,
            result.Delivered ? NotificationDeliveryOutcome.Sent : NotificationDeliveryOutcome.Failed,
            result.Detail);
    }

    /// <summary>
    /// Ticket and alert identities are whatever the module stores — a Keycloak subject for anything
    /// done in the browser, a username for anything seeded (WP-1.11). Both are tried, plus a directory
    /// id, so a preference set against a seeded technician still resolves.
    /// </summary>
    private async Task<string?> ResolveAddressAsync(string userId, CancellationToken cancellationToken)
    {
        if (userId.Contains('@', StringComparison.Ordinal))
        {
            return userId;
        }

        var byId = Guid.TryParse(userId, out var id) ? id : (Guid?)null;
        return await dbContext.UserProfiles.AsNoTracking()
            .Where(user => user.Username == userId || (byId != null && user.Id == byId))
            .Select(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static NotificationDelivery Record(
        NotificationEnvelope envelope,
        DateTimeOffset now,
        NotificationChannel? channel,
        NotificationChannelKind kind,
        string target,
        string? userId,
        Guid? ruleId,
        NotificationDeliveryOutcome outcome,
        string? detail,
        DateTimeOffset? releaseAfter = null) => new()
        {
            Id = Guid.CreateVersion7(),
            EventKind = envelope.EventKind,
            Severity = envelope.Severity,
            Subject = Clamp(envelope.Subject, 500),
            Body = Clamp(EmailNotificationChannel.RenderBody(envelope), 8_000),
            DeepLink = envelope.DeepLink,
            DedupeKey = envelope.DedupeKey,
            ChannelId = channel?.Id,
            ChannelKind = kind,
            TargetRedacted = NotificationChannel.Redact(kind, target),
            UserId = userId,
            RuleId = ruleId,
            Outcome = outcome,
            Detail = detail is null ? null : Clamp(detail, 2_000),
            ReleaseAfter = releaseAfter,
            OccurredAt = now,
            CompletedAt = outcome is NotificationDeliveryOutcome.Deferred ? null : now,
        };

    private static IReadOnlyList<string> Normalise(IReadOnlyCollection<string>? userIds) =>
        userIds is null
            ? []
            : [.. userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)];

    private static string Clamp(string value, int length) => value.Length <= length ? value : value[..length];
}
