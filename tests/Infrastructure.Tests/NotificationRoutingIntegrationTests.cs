using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Platform.Data;
using Platform.Notifications;

namespace Infrastructure.Tests;

/// <summary>
/// The routing engine against a real database: which channels a notification reaches, what quiet
/// hours do to it, and what the digest pass does afterwards. The channels themselves are recorded
/// rather than dispatched — what is under test is the decision, not SMTP.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class NotificationRoutingIntegrationTests(InfrastructureFixture infrastructure) : IAsyncLifetime
{
    private PlatformDbContext _dbContext = null!;
    private RecordingChannel _email = null!;
    private RecordingChannel _teams = null!;
    private string _suffix = null!;
    private string _eventKind = null!;
    private int _channelSequence;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(infrastructure.PostgresConnectionString)
            .Options;
        _dbContext = new PlatformDbContext(options);
        await _dbContext.Database.MigrateAsync();
        _email = new RecordingChannel(NotificationChannelKind.Email);
        _teams = new RecordingChannel(NotificationChannelKind.Teams);
        // This database is shared by the whole collection and routing rules are global by design, so
        // every rule a test writes is scoped to an event kind only that test publishes — otherwise one
        // test's channel receives every other test's notifications. Same class of trap as the WP-3.4
        // shared-table finding, and the symptom is a count that grows with the suite.
        // Version 4, not version 7: a v7 GUID opens with the millisecond timestamp, so the first eight
        // hex characters of every GUID created inside the same ~65 seconds are identical — which made
        // this "unique" suffix the same for every test in the run.
        _suffix = Guid.NewGuid().ToString("N")[..8];
        _eventKind = $"TestEvent-{_suffix}";
        _channelSequence = 0;
    }

    public Task DisposeAsync() => _dbContext.DisposeAsync().AsTask();

    /// <summary>
    /// The WP's two routing cases in one assertion, and the reason rules are additive rather than
    /// ordered: a Critical matches both minimums, a Warning matches only the lower one.
    /// </summary>
    [Fact]
    public async Task Route_ACriticalAndAWarning_ReachChatAndEmailRespectively()
    {
        var email = await ChannelAsync(NotificationChannelKind.Email, "ops@it-platform.local");
        var chat = await ChannelAsync(NotificationChannelKind.Teams, "https://example.test/hooks/abcd");
        await RuleAsync("warning-to-email", email, NotificationSeverity.Warning);
        await RuleAsync("critical-to-chat", chat, NotificationSeverity.Critical);

        var critical = await Router().RouteAsync(Envelope(NotificationSeverity.Critical), null, default);
        var warning = await Router().RouteAsync(Envelope(NotificationSeverity.Warning), null, default);

        Assert.Equal(2, critical.Sent);
        Assert.Equal(1, warning.Sent);
        Assert.Equal(2, _email.Sent.Count);
        Assert.Single(_teams.Sent);
        Assert.Equal(NotificationSeverity.Critical, _teams.Sent[0].Envelope.Severity);
    }

    /// <summary>A rule scoped to a device group is about devices and must not fire for an SLA breach.</summary>
    [Fact]
    public async Task Route_ARuleScopedToADeviceGroup_TakesOnlyItsOwnGroup()
    {
        var channel = await ChannelAsync(NotificationChannelKind.Email, "network@it-platform.local");
        var rule = await RuleAsync("edge-only", channel, NotificationSeverity.Warning);
        rule.DeviceGroup = "edge";
        await _dbContext.SaveChangesAsync();

        await Router().RouteAsync(Envelope(NotificationSeverity.Critical, deviceGroup: "edge"), null, default);
        await Router().RouteAsync(Envelope(NotificationSeverity.Critical, deviceGroup: "core"), null, default);
        await Router().RouteAsync(Envelope(NotificationSeverity.Critical, eventKind: "SlaBreached"), null, default);

        Assert.Single(_email.Sent);
        Assert.Equal("edge", _email.Sent[0].Envelope.DeviceGroup);
    }

    /// <summary>
    /// Quiet hours withhold rather than drop: nothing is sent, a Deferred row carries the release
    /// instant, and the digest pass turns the group into one message.
    /// </summary>
    [Fact]
    public async Task Route_InsideQuietHours_WithholdsThenSendsOneDigest()
    {
        var channel = await ChannelAsync(NotificationChannelKind.Email, "oncall@it-platform.local");
        var rule = await RuleAsync("always-quiet", channel, NotificationSeverity.Informational);
        // A window that covers the whole day but one minute: quiet whenever this test runs, without
        // the test having to know what time it is.
        rule.QuietHoursStart = new TimeOnly(0, 1);
        rule.QuietHoursEnd = new TimeOnly(0, 0);
        rule.DigestQuietHours = true;
        await _dbContext.SaveChangesAsync();

        await Router().RouteAsync(Envelope(NotificationSeverity.Critical, subject: "Host is unreachable"), null, default);
        await Router().RouteAsync(Envelope(NotificationSeverity.Critical, subject: "Host is unreachable"), null, default);
        await Router().RouteAsync(Envelope(NotificationSeverity.Warning, subject: "CPU is high", dedupeKey: "cpu"), null, default);

        var deferred = await DeliveriesAsync(channel.Id);
        Assert.Equal(3, deferred.Count);
        Assert.All(deferred, delivery => Assert.Equal(NotificationDeliveryOutcome.Deferred, delivery.Outcome));
        Assert.All(deferred, delivery => Assert.NotNull(delivery.ReleaseAfter));
        Assert.Empty(_email.Sent);

        // The pass is driven at a time past the release rather than by waiting for one.
        var release = deferred.Max(delivery => delivery.ReleaseAfter)!.Value.AddMinutes(1);
        var report = await Digest().RunAsync(release, default);

        Assert.Equal(1, report.Groups);
        Assert.Equal(3, report.Notifications);
        var sent = Assert.Single(_email.Sent);
        Assert.Equal("Quiet hours digest: 3 notification(s)", sent.Envelope.Subject);
        Assert.Contains("Host is unreachable (×2)", sent.Envelope.Body, StringComparison.Ordinal);

        var after = await DeliveriesAsync(channel.Id);
        var digestRow = Assert.Single(after, delivery => delivery.EventKind == NotificationDigestService.DigestEventKind);
        Assert.Equal(3, digestRow.DigestOfCount);
        Assert.All(after.Where(delivery => delivery.Id != digestRow.Id), delivery =>
        {
            Assert.Equal(NotificationDeliveryOutcome.Digested, delivery.Outcome);
            Assert.Equal(digestRow.Id, delivery.DigestDeliveryId);
        });
    }

    /// <summary>A rule that chose not to digest drops the message, and the row still says it happened.</summary>
    [Fact]
    public async Task Route_InsideQuietHoursThatDoNotDigest_SuppressesWithoutQueueing()
    {
        var channel = await ChannelAsync(NotificationChannelKind.Email, "lossy@it-platform.local");
        var rule = await RuleAsync("quiet-and-lossy", channel, NotificationSeverity.Informational);
        rule.QuietHoursStart = new TimeOnly(0, 1);
        rule.QuietHoursEnd = new TimeOnly(0, 0);
        rule.DigestQuietHours = false;
        await _dbContext.SaveChangesAsync();

        await Router().RouteAsync(Envelope(NotificationSeverity.Critical), null, default);

        var delivery = Assert.Single(await DeliveriesAsync(channel.Id));
        Assert.Equal(NotificationDeliveryOutcome.Suppressed, delivery.Outcome);
        Assert.Null(delivery.ReleaseAfter);
        Assert.Equal(0, (await Digest().RunAsync(DateTimeOffset.UtcNow.AddDays(1), default)).Groups);
    }

    /// <summary>
    /// Failure path. A channel that refuses the message is recorded as Failed and the call still
    /// returns — a broken webhook must not be able to fail the alert raise that caused it.
    /// </summary>
    [Fact]
    public async Task Route_WhenAChannelRefuses_RecordsAFailureAndDoesNotThrow()
    {
        var channel = await ChannelAsync(NotificationChannelKind.Teams, "https://example.test/hooks/dead");
        await RuleAsync("to-a-dead-hook", channel, NotificationSeverity.Informational);
        _teams.Failure = "403 Forbidden: the webhook has been revoked.";

        var report = await Router().RouteAsync(Envelope(NotificationSeverity.Critical), null, default);

        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Sent);
        var delivery = Assert.Single(await DeliveriesAsync(channel.Id));
        Assert.Equal(NotificationDeliveryOutcome.Failed, delivery.Outcome);
        Assert.Equal("403 Forbidden: the webhook has been revoked.", delivery.Detail);
    }

    /// <summary>The stored row must never carry the credential a read would refuse to return.</summary>
    [Fact]
    public async Task Route_ToAWebhook_StoresOnlyARedactedTarget()
    {
        var channel = await ChannelAsync(NotificationChannelKind.Teams, "https://example.test/hooks/s3cr3tvalue");
        await RuleAsync("redaction", channel, NotificationSeverity.Informational);

        await Router().RouteAsync(Envelope(NotificationSeverity.Warning), null, default);

        var delivery = Assert.Single(await DeliveriesAsync(channel.Id));
        Assert.DoesNotContain("s3cr3tvalue", delivery.TargetRedacted, StringComparison.Ordinal);
        Assert.StartsWith("https://example.test/", delivery.TargetRedacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// A user with no preference row hears everything by email — the behaviour every package before
    /// WP-3.10 had, which a new feature must not quietly take away.
    /// </summary>
    [Fact]
    public async Task Route_ToAUserWithNoPreference_StillEmailsTheDirectoryAddress()
    {
        var user = await UserAsync();

        var report = await Router().RouteAsync(Envelope(NotificationSeverity.Informational), [user.Username], default);

        Assert.Equal(1, report.Sent);
        Assert.Equal(user.Email, Assert.Single(_email.Sent).Target);
    }

    [Fact]
    public async Task Route_ToAUserWhoTurnedEmailOff_SuppressesAndSaysSo()
    {
        var user = await UserAsync();
        await PreferenceAsync(user.Username, preference => preference.EmailEnabled = false);

        var report = await Router().RouteAsync(Envelope(NotificationSeverity.Critical), [user.Username], default);

        Assert.Equal(1, report.Suppressed);
        Assert.Empty(_email.Sent);
        var delivery = Assert.Single(await UserDeliveriesAsync(user.Username));
        Assert.Equal(NotificationDeliveryOutcome.Suppressed, delivery.Outcome);
        Assert.Contains("turned email notifications off", delivery.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_ToAUserWhoWantsCriticalOnly_SkipsAWarning()
    {
        var user = await UserAsync();
        await PreferenceAsync(user.Username, preference => preference.MinimumSeverity = NotificationSeverity.Critical);

        await Router().RouteAsync(Envelope(NotificationSeverity.Warning), [user.Username], default);
        await Router().RouteAsync(Envelope(NotificationSeverity.Critical), [user.Username], default);

        Assert.Single(_email.Sent);
        Assert.Equal(NotificationSeverity.Critical, _email.Sent[0].Envelope.Severity);
    }

    /// <summary>
    /// A preference's own address beats the directory's, because the point of the field is that a
    /// technician can be paged somewhere other than their desk mailbox.
    /// </summary>
    [Fact]
    public async Task Route_ToAUserWithTheirOwnAddress_PrefersItOverTheDirectory()
    {
        var user = await UserAsync();
        await PreferenceAsync(user.Username, preference => preference.EmailAddress = "pager@it-platform.local");

        await Router().RouteAsync(Envelope(NotificationSeverity.Critical), [user.Username], default);

        Assert.Equal("pager@it-platform.local", Assert.Single(_email.Sent).Target);
    }

    /// <summary>
    /// Seeded helpdesk identities are usernames rather than directory ids (WP-1.11), so "nowhere to
    /// send it" is a real case. It is a suppression with a reason, not a delivery failure.
    /// </summary>
    [Fact]
    public async Task Route_ToAnIdentityTheDirectoryDoesNotKnow_SuppressesWithAReason()
    {
        var report = await Router().RouteAsync(Envelope(NotificationSeverity.Critical), ["nobody-here"], default);

        Assert.Equal(1, report.Suppressed);
        var delivery = Assert.Single(await UserDeliveriesAsync("nobody-here"));
        Assert.Contains("No email address is recorded", delivery.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two rules selecting one channel must not post the same message twice, and the collision has to
    /// resolve toward the operator hearing about it — the sending rule wins over the withholding one.
    /// </summary>
    [Fact]
    public async Task Route_WithTwoRulesOnOneChannel_SendsOnceAndPrefersTheOneThatSends()
    {
        var channel = await ChannelAsync(NotificationChannelKind.Email, "duplicated@it-platform.local");
        var quiet = await RuleAsync("a-quiet-rule", channel, NotificationSeverity.Informational);
        quiet.QuietHoursStart = new TimeOnly(0, 1);
        quiet.QuietHoursEnd = new TimeOnly(0, 0);
        await RuleAsync("b-awake-rule", channel, NotificationSeverity.Informational);
        await _dbContext.SaveChangesAsync();

        var report = await Router().RouteAsync(Envelope(NotificationSeverity.Critical), null, default);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Sent);
        Assert.Single(_email.Sent);
    }

    /// <summary>A deactivated channel routes nothing, and neither does a deactivated rule.</summary>
    [Fact]
    public async Task Route_WithAnInactiveChannelOrRule_ReachesNobody()
    {
        var inactiveChannel = await ChannelAsync(NotificationChannelKind.Email, "off@it-platform.local", active: false);
        await RuleAsync("live-rule-dead-channel", inactiveChannel, NotificationSeverity.Informational);
        var liveChannel = await ChannelAsync(NotificationChannelKind.Email, "on@it-platform.local");
        var deadRule = await RuleAsync("dead-rule", liveChannel, NotificationSeverity.Informational);
        deadRule.IsActive = false;
        await _dbContext.SaveChangesAsync();

        var report = await Router().RouteAsync(Envelope(NotificationSeverity.Critical), null, default);

        Assert.Equal(0, report.Total);
        Assert.Empty(_email.Sent);
    }

    /// <summary>
    /// A digest whose channel was deleted while its notifications were held cannot be delivered, and
    /// the contributors stay Deferred rather than being silently marked done.
    /// </summary>
    [Fact]
    public async Task Digest_WhenItsChannelHasBeenDeactivated_LeavesTheNotificationsHeld()
    {
        var channel = await ChannelAsync(NotificationChannelKind.Email, "vanishing@it-platform.local");
        var rule = await RuleAsync("vanishing-rule", channel, NotificationSeverity.Informational);
        rule.QuietHoursStart = new TimeOnly(0, 1);
        rule.QuietHoursEnd = new TimeOnly(0, 0);
        await _dbContext.SaveChangesAsync();
        await Router().RouteAsync(Envelope(NotificationSeverity.Critical), null, default);

        channel.IsActive = false;
        await _dbContext.SaveChangesAsync();
        var report = await Digest().RunAsync(DateTimeOffset.UtcNow.AddDays(1), default);

        Assert.Equal(0, report.Groups);
        Assert.Equal(1, report.Failed);
        Assert.All(await DeliveriesAsync(channel.Id),
            delivery => Assert.Equal(NotificationDeliveryOutcome.Deferred, delivery.Outcome));

        // And the retry the failure was left open for. This also clears the row: a Deferred row left
        // behind would be swept up by whichever test next runs a digest pass against this database.
        channel.IsActive = true;
        await _dbContext.SaveChangesAsync();
        Assert.Equal(1, (await Digest().RunAsync(DateTimeOffset.UtcNow.AddDays(1), default)).Groups);
        Assert.Contains(await DeliveriesAsync(channel.Id),
            delivery => delivery.Outcome == NotificationDeliveryOutcome.Digested);
    }

    private NotificationRouter Router() =>
        new(_dbContext, [_email, _teams], NullLogger<NotificationRouter>.Instance);

    private NotificationDigestService Digest() =>
        new(_dbContext, [_email, _teams], NullLogger<NotificationDigestService>.Instance);

    private NotificationEnvelope Envelope(
        NotificationSeverity severity,
        string? eventKind = null,
        string? deviceGroup = null,
        string subject = "Something happened",
        string? dedupeKey = null) =>
        new(eventKind ?? _eventKind, severity, subject, "Body", "https://it-platform.local/monitoring/alerts",
            deviceGroup, dedupeKey ?? subject);

    private async Task<NotificationChannel> ChannelAsync(
        NotificationChannelKind kind,
        string target,
        bool active = true)
    {
        var channel = new NotificationChannel
        {
            Id = Guid.CreateVersion7(),
            // The target is deliberately not part of the name: a channel name is returned by every
            // read, and putting a webhook URL in one would leak exactly what redaction exists to hide.
            Name = $"{kind}-{_suffix}-{++_channelSequence}",
            Kind = kind,
            Target = target,
            IsActive = active,
            CreatedBy = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.NotificationChannels.Add(channel);
        await _dbContext.SaveChangesAsync();
        return channel;
    }

    private async Task<NotificationRoutingRule> RuleAsync(
        string name,
        NotificationChannel channel,
        NotificationSeverity minimum)
    {
        var rule = new NotificationRoutingRule
        {
            Id = Guid.CreateVersion7(),
            Name = $"{name}-{_suffix}",
            ChannelId = channel.Id,
            Channel = channel,
            EventKind = _eventKind,
            MinimumSeverity = minimum,
            TimeZone = "UTC",
            DigestQuietHours = true,
            IsActive = true,
            CreatedBy = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.NotificationRoutingRules.Add(rule);
        await _dbContext.SaveChangesAsync();
        return rule;
    }

    private async Task<UserProfile> UserAsync()
    {
        var site = await _dbContext.Sites.FirstOrDefaultAsync();
        if (site is null)
        {
            site = new Site { Id = Guid.CreateVersion7(), Code = $"S{_suffix}", Name = "Test site" };
            _dbContext.Sites.Add(site);
        }

        var department = await _dbContext.Departments.FirstOrDefaultAsync();
        if (department is null)
        {
            department = new Department { Id = Guid.CreateVersion7(), Code = $"D{_suffix}", Name = "Test department" };
            _dbContext.Departments.Add(department);
        }

        var user = new UserProfile
        {
            Id = Guid.CreateVersion7(),
            Username = $"technician-{_suffix}",
            Email = $"technician-{_suffix}@it-platform.local",
            DisplayName = "Technician Under Test",
            Role = "Technician",
            SiteId = site.Id,
            DepartmentId = department.Id,
        };
        _dbContext.UserProfiles.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private async Task PreferenceAsync(string userId, Action<UserNotificationPreference> configure)
    {
        var preference = new UserNotificationPreference
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EmailEnabled = true,
            MinimumSeverity = NotificationSeverity.Informational,
            TimeZone = "UTC",
            DigestQuietHours = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        configure(preference);
        _dbContext.UserNotificationPreferences.Add(preference);
        await _dbContext.SaveChangesAsync();
    }

    private Task<List<NotificationDelivery>> DeliveriesAsync(Guid channelId) =>
        _dbContext.NotificationDeliveries.AsNoTracking()
            .Where(delivery => delivery.ChannelId == channelId)
            .OrderBy(delivery => delivery.OccurredAt)
            .ToListAsync();

    private Task<List<NotificationDelivery>> UserDeliveriesAsync(string userId) =>
        _dbContext.NotificationDeliveries.AsNoTracking()
            .Where(delivery => delivery.UserId == userId)
            .ToListAsync();

    /// <summary>
    /// Records what it was handed instead of dispatching it. The channels have their own tests; what
    /// these need to observe is the routing decision.
    /// </summary>
    private sealed class RecordingChannel(NotificationChannelKind kind) : INotificationChannel
    {
        public NotificationChannelKind Kind => kind;

        public string? Failure { get; set; }

        public List<(string Target, NotificationEnvelope Envelope)> Sent { get; } = [];

        public Task<NotificationDispatchResult> SendAsync(
            string target,
            NotificationEnvelope envelope,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromResult(NotificationDispatchResult.Failure(Failure));
            }

            Sent.Add((target, envelope));
            return Task.FromResult(NotificationDispatchResult.Success());
        }
    }
}
