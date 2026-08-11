namespace Platform.Data;

/// <summary>
/// How a notification leaves the platform. The kind decides the wire format, not the destination —
/// Teams and Slack are both "POST this JSON to a URL", and they are separate members because the two
/// bodies are different documents, not because one is more configurable than the other.
/// </summary>
public enum NotificationChannelKind
{
    Email,
    Teams,
    Slack,
}

/// <summary>
/// The platform's own severity scale, deliberately not <c>AlertSeverity</c>: Monitoring owns that one
/// and Platform may not reference a module. Ordered lowest-first so a rule's minimum is a
/// <c>&gt;=</c> comparison rather than a lookup table.
/// </summary>
public enum NotificationSeverity
{
    Informational,
    Warning,
    Critical,
}

/// <summary>What happened to one notification on one channel. Every one of these is a stored row.</summary>
public enum NotificationDeliveryOutcome
{
    /// <summary>Handed to the channel and accepted by it.</summary>
    Sent,

    /// <summary>The channel refused it. The reason is in <see cref="NotificationDelivery.Detail"/>.</summary>
    Failed,

    /// <summary>Withheld by a quiet-hours schedule and waiting for <see cref="NotificationDelivery.ReleaseAfter"/>.</summary>
    Deferred,

    /// <summary>Withheld, released, and rolled into the digest named by <see cref="NotificationDelivery.DigestDeliveryId"/>.</summary>
    Digested,

    /// <summary>Withheld by a schedule that does not digest, or by a preference that declined it. Never sent.</summary>
    Suppressed,
}

/// <summary>
/// A place notifications go. The <see cref="Target"/> is an email address for
/// <see cref="NotificationChannelKind.Email"/> and an incoming-webhook URL otherwise — and a webhook
/// URL is a credential (anyone holding it can post into the channel), so no API ever returns it. See
/// <see cref="Redact"/>; the real vault is WP-3.11.
/// </summary>
public sealed class NotificationChannel
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public NotificationChannelKind Kind { get; set; }

    public required string Target { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<NotificationRoutingRule> Rules { get; set; } = [];

    /// <summary>
    /// Enough to recognise which channel a row means, and never enough to post to it. An email keeps
    /// its domain; a webhook keeps its host and the last four characters of its path, which is what
    /// distinguishes two Teams hooks in the same tenant.
    /// </summary>
    public static string Redact(NotificationChannelKind kind, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        if (kind is NotificationChannelKind.Email)
        {
            var at = target.IndexOf('@', StringComparison.Ordinal);
            return at <= 0 ? "***" : $"***@{target[(at + 1)..]}";
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return "***";
        }

        var tail = uri.AbsolutePath.TrimEnd('/');
        tail = tail.Length <= 4 ? tail : tail[^4..];
        // Authority, not Host: the port is part of what distinguishes two endpoints on one machine,
        // and dropping it made a dev sink on :9099 read identically to one on :9100.
        return $"{uri.Scheme}://{uri.Authority}/…{tail}";
    }
}

/// <summary>
/// One routing rule: which notifications reach which channel, and when the channel is allowed to be
/// woken. Rules are <em>additive</em> — every rule that matches fires, deduped by channel — because
/// "Critical goes to Teams and email, Warning goes to email only" is two rules with different
/// minimums rather than an ordering somebody has to get right.
/// </summary>
public sealed class NotificationRoutingRule
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid ChannelId { get; set; }

    public NotificationChannel Channel { get; set; } = null!;

    /// <summary>
    /// The event kind this rule answers (<c>AlertRaised</c>, <c>SlaBreached</c>, …), or null for any.
    /// A free string rather than an enum because the set is open: the module that publishes a new kind
    /// of notification must not need a Platform enum member to route it.
    /// </summary>
    public string? EventKind { get; set; }

    public NotificationSeverity MinimumSeverity { get; set; }

    /// <summary>
    /// The poller group of the device the notification is about, or null for any. Monitoring resolves
    /// it and puts it on the envelope; Platform never reads a monitoring table to find out.
    /// </summary>
    public string? DeviceGroup { get; set; }

    /// <summary>Start of the daily quiet window in <see cref="TimeZone"/>, or null for "never quiet".</summary>
    public TimeOnly? QuietHoursStart { get; set; }

    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>IANA id. Quiet hours are a human's evening, so they are a wall-clock fact somewhere.</summary>
    public required string TimeZone { get; set; }

    /// <summary>
    /// True to hold a quiet-hours notification and send one roll-up when the window ends; false to
    /// drop it. A rule that drops is a rule somebody chose to be lossy, and the delivery row still
    /// records that it happened.
    /// </summary>
    public bool DigestQuietHours { get; set; }

    public bool IsActive { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// What one person wants to hear about. Applies only to notifications addressed to a user — an SLA
/// warning for the technician who holds the ticket — and never to a routing rule's channel, which is
/// a team's and is nobody's to mute.
/// </summary>
public sealed class UserNotificationPreference
{
    public Guid Id { get; set; }

    /// <summary>The identity subject, matching how tickets store an assignee (WP-1.7).</summary>
    public required string UserId { get; set; }

    /// <summary>
    /// Where to reach them. Null falls back to the directory's address for the user, which is what a
    /// preference row created by hand for a seeded username will do.
    /// </summary>
    public string? EmailAddress { get; set; }

    public bool EmailEnabled { get; set; }

    public NotificationSeverity MinimumSeverity { get; set; }

    public TimeOnly? QuietHoursStart { get; set; }

    public TimeOnly? QuietHoursEnd { get; set; }

    public required string TimeZone { get; set; }

    public bool DigestQuietHours { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The durable record of one notification on its way to one destination, following WP-2.6's rule that
/// the row is the notification: delivery itself is best-effort, so what a run actually did has to be
/// readable afterwards rather than inferred from a log.
/// <para>
/// It is also the digest queue. A withheld notification is a <see cref="NotificationDeliveryOutcome.Deferred"/>
/// row carrying its own release time; the digest pass turns a group of them into one
/// <see cref="NotificationDeliveryOutcome.Sent"/> row and marks each contributor
/// <see cref="NotificationDeliveryOutcome.Digested"/>. Two tables would have had to agree about the
/// same fact.
/// </para>
/// </summary>
public sealed class NotificationDelivery
{
    public Guid Id { get; set; }

    public required string EventKind { get; set; }

    public NotificationSeverity Severity { get; set; }

    public required string Subject { get; set; }

    public required string Body { get; set; }

    public string? DeepLink { get; set; }

    /// <summary>
    /// The notification's identity as its publisher sees it (<c>alert:{alertId}:raised</c>). Not
    /// unique — the same fact legitimately goes to several channels — but it is what makes a digest
    /// able to say "3 messages about this alert" rather than listing three subjects.
    /// </summary>
    public string? DedupeKey { get; set; }

    public Guid? ChannelId { get; set; }

    public NotificationChannel? Channel { get; set; }

    public NotificationChannelKind ChannelKind { get; set; }

    /// <summary>The destination as <see cref="NotificationChannel.Redact"/> renders it. Never the real one.</summary>
    public required string TargetRedacted { get; set; }

    /// <summary>Set when this delivery was addressed to a person rather than a team channel.</summary>
    public string? UserId { get; set; }

    public Guid? RuleId { get; set; }

    public NotificationDeliveryOutcome Outcome { get; set; }

    public string? Detail { get; set; }

    /// <summary>When a deferred row becomes eligible for the digest pass. Null unless Deferred.</summary>
    public DateTimeOffset? ReleaseAfter { get; set; }

    /// <summary>The digest this row was rolled into, set when it turns Digested.</summary>
    public Guid? DigestDeliveryId { get; set; }

    /// <summary>How many deferred rows this row is the digest of. Null on everything that is not one.</summary>
    public int? DigestOfCount { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
