using Microsoft.EntityFrameworkCore;

using Platform.Data;

namespace Platform.Seeding;

public sealed record NotificationSeedResult(int ChannelsAdded, int RulesAdded, bool WebhookSeeded);

/// <summary>
/// A fresh <c>aspire run</c> has to be able to show routing working, and the dev database is recreated
/// on most AppHost restarts (the WP-1.9 decision), so the fixtures have to be seeded rather than made
/// by hand.
/// <para>
/// The email side is seeded unconditionally — MailHog is always there. The webhook side is seeded
/// <em>only</em> when a URL is supplied: a placeholder webhook channel would fail every Critical alert
/// on a machine with no such endpoint, which reads as a broken feature rather than an unconfigured
/// one. That is the same call WP-3.8 made about not seeding a TLS check.
/// </para>
/// </summary>
public sealed class NotificationRoutingSeeder(PlatformDbContext dbContext)
{
    public const string OperationsEmailChannel = "IT Operations email";
    public const string ChatChannel = "IT Operations chat";
    private const string SeedActor = "system:seeder";

    /// <param name="operationsEmail">Where the seeded email channel points. MailHog captures it in dev.</param>
    /// <param name="webhookUrl">A Teams or Slack incoming webhook, or null to seed no chat channel.</param>
    /// <param name="webhookKind">Which document to post. Ignored when no URL is given.</param>
    public async Task<NotificationSeedResult> SeedAsync(
        string operationsEmail = "it-operations@it-platform.local",
        string? webhookUrl = null,
        NotificationChannelKind webhookKind = NotificationChannelKind.Teams,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var channelsAdded = 0;
        var rulesAdded = 0;

        var email = await EnsureChannelAsync(
            Id(1), OperationsEmailChannel, NotificationChannelKind.Email, operationsEmail,
            "Everything the platform decides is worth an email. Captured by MailHog in development.",
            now, cancellationToken);
        channelsAdded += email.Added ? 1 : 0;

        NotificationChannel? chat = null;
        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            var seeded = await EnsureChannelAsync(
                Id(2), ChatChannel, webhookKind, webhookUrl,
                "Critical alerts only. Configured through Notifications:Seed:WebhookUrl.",
                now, cancellationToken);
            chat = seeded.Channel;
            channelsAdded += seeded.Added ? 1 : 0;
        }

        // The WP's two verification cases, spelled as two rules rather than an ordering: a Warning
        // matches only the email rule, a Critical matches both.
        rulesAdded += await EnsureRuleAsync(
            Id(11), "Warning and above to email", email.Channel, NotificationSeverity.Warning, now,
            cancellationToken)
            ? 1 : 0;

        if (chat is not null)
        {
            rulesAdded += await EnsureRuleAsync(
                Id(12), "Critical to chat", chat, NotificationSeverity.Critical, now, cancellationToken)
                ? 1 : 0;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new(channelsAdded, rulesAdded, chat is not null);
    }

    private async Task<(NotificationChannel Channel, bool Added)> EnsureChannelAsync(
        Guid id,
        string name,
        NotificationChannelKind kind,
        string target,
        string description,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.NotificationChannels
            .SingleOrDefaultAsync(channel => channel.Id == id, cancellationToken);
        if (existing is not null)
        {
            // A re-run refreshes the destination — the webhook URL is exactly the kind of thing an
            // operator changes between runs — and leaves everything else, including whether somebody
            // has since deactivated it, alone.
            existing.Target = target;
            return (existing, false);
        }

        var channel = new NotificationChannel
        {
            Id = id,
            Name = name,
            Kind = kind,
            Target = target,
            Description = description,
            IsActive = true,
            CreatedBy = SeedActor,
            CreatedAt = now,
            UpdatedBy = SeedActor,
            UpdatedAt = now,
        };
        dbContext.NotificationChannels.Add(channel);
        return (channel, true);
    }

    private async Task<bool> EnsureRuleAsync(
        Guid id,
        string name,
        NotificationChannel channel,
        NotificationSeverity minimumSeverity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await dbContext.NotificationRoutingRules.AnyAsync(rule => rule.Id == id, cancellationToken))
        {
            return false;
        }

        dbContext.NotificationRoutingRules.Add(new NotificationRoutingRule
        {
            Id = id,
            Name = name,
            ChannelId = channel.Id,
            Channel = channel,
            // No event kind and no device group: the seeded rules are about severity, which is what
            // makes them fire for an alert and for an SLA breach alike.
            EventKind = null,
            MinimumSeverity = minimumSeverity,
            DeviceGroup = null,
            // No quiet hours by default. A seeded window would silence the demo for part of the day
            // and look like the platform had stopped working; the checklist sets one by hand.
            TimeZone = "UTC",
            DigestQuietHours = true,
            IsActive = true,
            CreatedBy = SeedActor,
            CreatedAt = now,
            UpdatedBy = SeedActor,
            UpdatedAt = now,
        });
        return true;
    }

    private static Guid Id(int sequence) =>
        Guid.Parse($"01980000-0000-7000-8000-00000000{sequence + 700:0000}");
}
