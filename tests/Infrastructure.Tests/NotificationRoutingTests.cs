using System.Text.Json.Nodes;

using Platform.Data;
using Platform.Notifications;

namespace Infrastructure.Tests;

/// <summary>
/// WP-3.10's decisions that need no infrastructure: when a schedule is quiet, which rules a
/// notification matches, what each channel puts on the wire, and what a digest says.
/// </summary>
public sealed class NotificationRoutingTests
{
    private static readonly DateTimeOffset Midday = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QuietHours_WithNoWindow_AreNeverQuiet()
    {
        var schedule = new QuietHoursSchedule(null, null, "UTC");

        Assert.False(schedule.IsConfigured);
        Assert.False(schedule.Evaluate(Midday).IsQuiet);
    }

    /// <summary>Half a window is not a window; the writes refuse it and the evaluation ignores it.</summary>
    [Fact]
    public void QuietHours_WithOnlyOneEnd_AreNeverQuiet()
    {
        var schedule = new QuietHoursSchedule(new TimeOnly(22, 0), null, "UTC");

        Assert.False(schedule.IsConfigured);
        Assert.False(schedule.Evaluate(new DateTimeOffset(2026, 8, 11, 23, 0, 0, TimeSpan.Zero)).IsQuiet);
    }

    [Theory]
    [InlineData(9, 0, false)]
    [InlineData(13, 0, true)]
    [InlineData(12, 0, true)]
    [InlineData(17, 0, false)]
    public void QuietHours_WithinTheDay_AreQuietBetweenTheEnds(int hour, int minute, bool expected)
    {
        var schedule = new QuietHoursSchedule(new TimeOnly(10, 0), new TimeOnly(17, 0), "UTC");

        var verdict = schedule.Evaluate(new DateTimeOffset(2026, 8, 11, hour, minute, 0, TimeSpan.Zero));

        Assert.Equal(expected, verdict.IsQuiet);
    }

    /// <summary>The common case: quiet hours are somebody's night, so they cross midnight.</summary>
    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    public void QuietHours_CrossingMidnight_AreQuietOnBothSidesOfIt(int hour, int minute, bool expected)
    {
        var schedule = new QuietHoursSchedule(new TimeOnly(22, 0), new TimeOnly(7, 0), "UTC");

        var verdict = schedule.Evaluate(new DateTimeOffset(2026, 8, 11, hour, minute, 0, TimeSpan.Zero));

        Assert.Equal(expected, verdict.IsQuiet);
    }

    /// <summary>
    /// The release instant is what the digest pass waits for, so it has to land on the *next* end of
    /// the window rather than on today's — the whole point of an evening window is that it ends
    /// tomorrow morning.
    /// </summary>
    [Fact]
    public void QuietHours_BeforeMidnight_ReleaseOnTheFollowingMorning()
    {
        var schedule = new QuietHoursSchedule(new TimeOnly(22, 0), new TimeOnly(7, 0), "UTC");

        var verdict = schedule.Evaluate(new DateTimeOffset(2026, 8, 11, 23, 30, 0, TimeSpan.Zero));

        Assert.True(verdict.IsQuiet);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 7, 0, 0, TimeSpan.Zero), verdict.ReleaseAfter);
    }

    [Fact]
    public void QuietHours_AfterMidnight_ReleaseTheSameMorning()
    {
        var schedule = new QuietHoursSchedule(new TimeOnly(22, 0), new TimeOnly(7, 0), "UTC");

        var verdict = schedule.Evaluate(new DateTimeOffset(2026, 8, 12, 2, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 8, 12, 7, 0, 0, TimeSpan.Zero), verdict.ReleaseAfter);
    }

    /// <summary>
    /// Quiet hours are a wall-clock fact somewhere. 23:00 in Tokyo is 14:00 UTC, and a schedule that
    /// evaluated in UTC would wake the wrong people.
    /// </summary>
    [Fact]
    public void QuietHours_AreEvaluatedInTheirOwnTimeZone()
    {
        var schedule = new QuietHoursSchedule(new TimeOnly(22, 0), new TimeOnly(7, 0), "Asia/Tokyo");

        Assert.True(schedule.Evaluate(new DateTimeOffset(2026, 8, 11, 14, 0, 0, TimeSpan.Zero)).IsQuiet);
        Assert.False(schedule.Evaluate(new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero)).IsQuiet);
    }

    /// <summary>
    /// Failure path. A typo in an operator-entered zone must not be able to throw on the alerting
    /// path — the fallback is wrong by hours, a throw is wrong by everything.
    /// </summary>
    [Fact]
    public void QuietHours_WithAnUnknownTimeZone_FallBackToUtcRatherThanThrow()
    {
        var schedule = new QuietHoursSchedule(new TimeOnly(22, 0), new TimeOnly(7, 0), "Middle/Earth");

        Assert.False(QuietHoursSchedule.IsKnownZone("Middle/Earth"));
        Assert.True(schedule.Evaluate(new DateTimeOffset(2026, 8, 11, 23, 0, 0, TimeSpan.Zero)).IsQuiet);
    }

    [Fact]
    public void Matches_ARuleBelowTheSeverity_TakesTheNotification()
    {
        var rule = Rule(NotificationSeverity.Warning);

        Assert.True(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Critical)));
        Assert.True(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Warning)));
        Assert.False(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Informational)));
    }

    [Fact]
    public void Matches_ARuleNamingAnEventKind_TakesOnlyThatKind()
    {
        var rule = Rule(NotificationSeverity.Informational);
        rule.EventKind = "AlertRaised";

        Assert.True(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Warning, eventKind: "AlertRaised")));
        Assert.False(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Warning, eventKind: "SlaBreached")));
    }

    /// <summary>
    /// A rule scoped to a device group is about devices. An SLA breach carries no group, and treating
    /// "no group" as "every group" would page the network team about a ticket.
    /// </summary>
    [Fact]
    public void Matches_ARuleNamingADeviceGroup_SkipsANotificationWithNoGroup()
    {
        var rule = Rule(NotificationSeverity.Informational);
        rule.DeviceGroup = "default";

        Assert.True(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Critical, deviceGroup: "default")));
        Assert.True(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Critical, deviceGroup: "DEFAULT")));
        Assert.False(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Critical, deviceGroup: "edge")));
        Assert.False(NotificationRouter.Matches(rule, Envelope(NotificationSeverity.Critical)));
    }

    /// <summary>
    /// A webhook URL is a bearer credential (ARCHITECTURE §7). Redaction has to leave enough to tell
    /// two hooks in one tenant apart and never enough to post to either.
    /// </summary>
    [Fact]
    public void Redact_AWebhookUrl_KeepsTheHostAndDropsTheSecret()
    {
        var redacted = NotificationChannel.Redact(
            NotificationChannelKind.Teams,
            "https://contoso.webhook.office.com/webhookb2/abc-def/IncomingWebhook/0123456789abcdef/ghij");

        Assert.Equal("https://contoso.webhook.office.com/…ghij", redacted);
        Assert.DoesNotContain("0123456789abcdef", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Found by hand-verification: the port is part of what distinguishes two endpoints on one host,
    /// and a redaction that dropped it made a dev sink on :9099 read identically to one on :9100.
    /// </summary>
    [Fact]
    public void Redact_AWebhookUrlOnANonDefaultPort_KeepsThePort()
    {
        Assert.Equal("http://localhost:9099/…bbbb",
            NotificationChannel.Redact(NotificationChannelKind.Teams, "http://localhost:9099/webhookb2/aaa/SECRET/bbbb"));
    }

    [Fact]
    public void Redact_AnEmailAddress_KeepsOnlyTheDomain()
    {
        Assert.Equal("***@it-platform.local",
            NotificationChannel.Redact(NotificationChannelKind.Email, "oncall@it-platform.local"));
    }

    [Fact]
    public void TeamsPayload_CarriesTheCardTeamsNeedsAndTheDeepLinkAsAButton()
    {
        var payload = WebhookNotificationChannel.BuildTeamsPayload(
            Envelope(NotificationSeverity.Critical) with
            {
                Subject = "Host is unreachable",
                DeepLink = "https://it-platform.local/monitoring/alerts?alertId=42",
                Facts = [new NotificationFact("Owner", "Technician Two")],
            });

        Assert.Equal("MessageCard", (string?)payload["@type"]);
        // Teams drops a card with no summary, and the summary is the toast the operator actually sees.
        Assert.Equal("Host is unreachable", (string?)payload["summary"]);
        Assert.Equal("D93025", (string?)payload["themeColor"]);
        var action = payload["potentialAction"]!.AsArray()[0]!;
        Assert.Equal("OpenUri", (string?)action["@type"]);
        Assert.Equal("https://it-platform.local/monitoring/alerts?alertId=42",
            (string?)action["targets"]!.AsArray()[0]!["uri"]);
        var facts = payload["sections"]!.AsArray()[0]!["facts"]!.AsArray();
        Assert.Equal("Owner", (string?)facts[0]!["name"]);
    }

    [Fact]
    public void SlackPayload_CarriesFallbackTextBesideItsBlocks()
    {
        var payload = WebhookNotificationChannel.BuildSlackPayload(
            Envelope(NotificationSeverity.Warning) with
            {
                Subject = "CPU is high",
                DeepLink = "https://it-platform.local/monitoring/alerts?alertId=7",
            });

        Assert.Equal("[Warning] CPU is high", (string?)payload["text"]);
        var blocks = payload["blocks"]!.AsArray();
        Assert.Contains("<https://it-platform.local/monitoring/alerts?alertId=7|Open in IT Platform>",
            blocks.Select(block => (string?)block!["elements"]?.AsArray()[0]?["text"]));
    }

    /// <summary>Slack refuses a section with more than ten fields, and losing the extras beats losing the message.</summary>
    [Fact]
    public void SlackPayload_WithManyFacts_TruncatesRatherThanBeingRefused()
    {
        var facts = Enumerable.Range(0, 15).Select(index => new NotificationFact($"Fact {index}", "value")).ToArray();

        var payload = WebhookNotificationChannel.BuildSlackPayload(Envelope(NotificationSeverity.Warning) with { Facts = facts });

        var fields = payload["blocks"]!.AsArray()
            .First(block => block!["fields"] is not null)!["fields"]!.AsArray();
        Assert.Equal(10, fields.Count);
    }

    [Fact]
    public void SlackPayload_EscapesTheThreeCharactersMrkdwnReserves()
    {
        var payload = WebhookNotificationChannel.BuildSlackPayload(
            Envelope(NotificationSeverity.Warning) with { Subject = "a<b>c&d" });

        var text = (string?)payload["blocks"]!.AsArray()[0]!["text"]!["text"];
        Assert.Contains("a&lt;b&gt;c&amp;d", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Found by hand-verification: an alert subject already opens with its severity, so a digest line
    /// that added its own read "[Critical] [Critical] …". SLA subjects carry no prefix, which is why
    /// the digest still states severity itself rather than trusting the subject.
    /// </summary>
    [Fact]
    public void Digest_OfSubjectsThatAlreadyStateSeverity_DoesNotSayItTwice()
    {
        var digest = NotificationDigestService.BuildDigest(
        [
            Deferred("[Critical] Host is unreachable", NotificationSeverity.Critical, "alert:1:raised"),
            Deferred("SLA breach on INC-000042", NotificationSeverity.Critical, "ticket:2:sla"),
        ]);

        Assert.Contains("[Critical] Host is unreachable", digest.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("[Critical] [Critical]", digest.Body, StringComparison.Ordinal);
        // A subject with no prefix of its own still gets one.
        Assert.Contains("[Critical] SLA breach on INC-000042", digest.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Critical] Host is unreachable", "Host is unreachable")]
    [InlineData("SLA breach on INC-000042", "SLA breach on INC-000042")]
    [InlineData("Disk [sda] is full", "Disk [sda] is full")]
    [InlineData("[unterminated", "[unterminated")]
    public void StripSeverityPrefix_RemovesOnlyALeadingBracketedWord(string subject, string expected) =>
        Assert.Equal(expected, NotificationDigestService.StripSeverityPrefix(subject));

    /// <summary>
    /// The point of a digest: eleven notifications about one flapping rule read as one line with an
    /// eleven on it, not as eleven lines.
    /// </summary>
    [Fact]
    public void Digest_CollapsesRepeatsOfOneProblemIntoACountedLine()
    {
        var items = new List<NotificationDelivery>
        {
            Deferred("Host is unreachable", NotificationSeverity.Critical, "alert:1:raised"),
            Deferred("Host is unreachable", NotificationSeverity.Critical, "alert:1:raised"),
            Deferred("Host is unreachable", NotificationSeverity.Critical, "alert:1:raised"),
            Deferred("CPU is high", NotificationSeverity.Warning, "alert:2:raised"),
        };

        var digest = NotificationDigestService.BuildDigest(items);

        Assert.Equal(NotificationSeverity.Critical, digest.Severity);
        Assert.Equal("Quiet hours digest: 4 notification(s)", digest.Subject);
        Assert.Contains("[Critical] Host is unreachable (×3)", digest.Body, StringComparison.Ordinal);
        Assert.Contains("[Warning] CPU is high", digest.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("CPU is high (×", digest.Body, StringComparison.Ordinal);
    }

    /// <summary>A roll-up of several problems has no one page of its own, so it carries no link.</summary>
    [Fact]
    public void Digest_OfSeveralProblems_CarriesNoDeepLink()
    {
        var digest = NotificationDigestService.BuildDigest(
        [
            Deferred("Host is unreachable", NotificationSeverity.Critical, "alert:1:raised", "https://example.test/1"),
            Deferred("CPU is high", NotificationSeverity.Warning, "alert:2:raised", "https://example.test/2"),
        ]);

        Assert.Null(digest.DeepLink);
    }

    private static NotificationRoutingRule Rule(NotificationSeverity minimum) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "Rule",
        MinimumSeverity = minimum,
        TimeZone = "UTC",
        CreatedBy = "test",
        UpdatedBy = "test",
    };

    private static NotificationEnvelope Envelope(
        NotificationSeverity severity,
        string eventKind = "AlertRaised",
        string? deviceGroup = null) =>
        new(eventKind, severity, "Subject", "Body", DeviceGroup: deviceGroup);

    private static NotificationDelivery Deferred(
        string subject,
        NotificationSeverity severity,
        string dedupeKey,
        string? deepLink = null) => new()
        {
            Id = Guid.CreateVersion7(),
            EventKind = "AlertRaised",
            Severity = severity,
            Subject = subject,
            Body = subject,
            DedupeKey = dedupeKey,
            DeepLink = deepLink,
            ChannelKind = NotificationChannelKind.Email,
            TargetRedacted = "***@example.test",
            Outcome = NotificationDeliveryOutcome.Deferred,
            OccurredAt = Midday,
        };
}
