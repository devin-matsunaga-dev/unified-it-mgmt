using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Platform.Data;

namespace Platform.Notifications;

/// <summary>One labelled value on a notification. Rendered as a fact card, a Slack field or a text line.</summary>
public sealed record NotificationFact(string Label, string Value);

/// <summary>
/// One notification, in the only form Platform understands: channel-agnostic. The publisher says what
/// happened and how bad it is; nothing here knows about alerts, tickets, Teams or SMTP.
/// </summary>
/// <param name="EventKind">
/// <c>AlertRaised</c>, <c>SlaBreached</c>, … Matched against a rule's <see cref="NotificationRoutingRule.EventKind"/>
/// and stored on the delivery row, so it is also the word an operator filters the history by.
/// </param>
/// <param name="DeviceGroup">
/// The poller group of the device this is about, where there is one. Monitoring resolves it — Platform
/// may not read a monitoring table, so an envelope that omits it simply matches every rule that does
/// not name a group.
/// </param>
/// <param name="DedupeKey">
/// Stable across the several channels one notification reaches, so a digest can count messages about
/// one problem instead of listing them.
/// </param>
public sealed record NotificationEnvelope(
    string EventKind,
    NotificationSeverity Severity,
    string Subject,
    string Body,
    string? DeepLink = null,
    string? DeviceGroup = null,
    string? DedupeKey = null,
    IReadOnlyList<NotificationFact>? Facts = null)
{
    public IReadOnlyList<NotificationFact> FactList => Facts ?? [];
}

/// <param name="Detail">Why it failed, in one sentence fit for a delivery row an operator reads.</param>
public readonly record struct NotificationDispatchResult(bool Delivered, string? Detail)
{
    public static NotificationDispatchResult Success(string? detail = null) => new(true, detail);

    public static NotificationDispatchResult Failure(string detail) => new(false, detail);
}

/// <summary>
/// A way out of the platform. Implementations never throw: a channel that cannot deliver returns a
/// failure the router records, because a broken webhook must not be able to fail the alert raise that
/// caused it — the same rule WP-3.9 applied to the SignalR broadcast.
/// </summary>
public interface INotificationChannel
{
    NotificationChannelKind Kind { get; }

    Task<NotificationDispatchResult> SendAsync(
        string target,
        NotificationEnvelope envelope,
        CancellationToken cancellationToken);
}

/// <summary>
/// Email, over the SMTP service that has carried every notification since WP-0.4. It renders the
/// envelope to text here rather than pushing a template through, because the subject and body arrive
/// already written by whoever raised the notification.
/// </summary>
public sealed class EmailNotificationChannel(INotificationService notificationService, ILogger<EmailNotificationChannel> logger)
    : INotificationChannel
{
    public NotificationChannelKind Kind => NotificationChannelKind.Email;

    public async Task<NotificationDispatchResult> SendAsync(
        string target,
        NotificationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            await notificationService.SendAsync(
                new NotificationMessage(
                    target,
                    // The model is empty on purpose: the template placeholders were substituted by the
                    // publisher, and a stray "{{Foo}}" in an alert summary must reach the reader intact
                    // rather than be silently blanked.
                    new NotificationTemplate(envelope.EventKind, envelope.Subject, RenderBody(envelope)),
                    new { }),
                cancellationToken);
            return NotificationDispatchResult.Success();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Email notification {EventKind} to {Target} failed.",
                envelope.EventKind, NotificationChannel.Redact(NotificationChannelKind.Email, target));
            return NotificationDispatchResult.Failure(OneLine(exception));
        }
    }

    public static string RenderBody(NotificationEnvelope envelope)
    {
        var lines = new List<string> { envelope.Body };
        if (envelope.FactList.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(envelope.FactList.Select(fact => $"{fact.Label}: {fact.Value}"));
        }

        if (!string.IsNullOrWhiteSpace(envelope.DeepLink))
        {
            lines.Add(string.Empty);
            lines.Add(envelope.DeepLink);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string OneLine(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}".ReplaceLineEndings(" ");
}

/// <summary>
/// Teams and Slack incoming webhooks: the same POST with two different documents. There is no SDK for
/// either — both are a JSON body to a URL — so the payload builders are here and unit-tested rather
/// than hidden behind a dependency.
/// </summary>
public sealed class WebhookNotificationChannel(
    NotificationChannelKind kind,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookNotificationChannel> logger) : INotificationChannel
{
    /// <summary>
    /// A webhook is a third party on the alerting path. Long enough for a slow tenant, short enough
    /// that a hung endpoint cannot hold a consumer open until its prefetch is exhausted — which is
    /// exactly the failure mode the WP-3.6 walk found behind a dead Redis.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public NotificationChannelKind Kind => kind;

    public async Task<NotificationDispatchResult> SendAsync(
        string target,
        NotificationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return NotificationDispatchResult.Failure($"'{NotificationChannel.Redact(kind, target)}' is not an http(s) webhook URL.");
        }

        var payload = kind is NotificationChannelKind.Slack ? BuildSlackPayload(envelope) : BuildTeamsPayload(envelope);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.PostAsJsonAsync(uri, payload, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                return NotificationDispatchResult.Success();
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return NotificationDispatchResult.Failure(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 200)}".Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Kind} webhook {Target} did not answer within {Seconds}s.",
                kind, NotificationChannel.Redact(kind, target), Timeout.TotalSeconds);
            return NotificationDispatchResult.Failure($"The webhook did not answer within {Timeout.TotalSeconds:0}s.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "{Kind} webhook {Target} failed.",
                kind, NotificationChannel.Redact(kind, target));
            return NotificationDispatchResult.Failure(EmailNotificationChannel.OneLine(exception));
        }
    }

    /// <summary>
    /// The legacy MessageCard, not an Adaptive Card. Incoming webhooks accept both, MessageCard needs
    /// no schema version negotiation, and its <c>potentialAction</c> is the only thing that renders
    /// the deep link as a button rather than a bare URL in the message text.
    /// </summary>
    public static JsonNode BuildTeamsPayload(NotificationEnvelope envelope)
    {
        var facts = new JsonArray();
        foreach (var fact in envelope.FactList)
        {
            facts.Add(new JsonObject { ["name"] = fact.Label, ["value"] = fact.Value });
        }

        var card = new JsonObject
        {
            ["@type"] = "MessageCard",
            ["@context"] = "https://schema.org/extensions",
            ["themeColor"] = ThemeColour(envelope.Severity),
            // Teams drops a card with no summary, and the summary is what the notification toast shows.
            ["summary"] = envelope.Subject,
            ["title"] = envelope.Subject,
            ["text"] = envelope.Body,
            ["sections"] = new JsonArray(new JsonObject
            {
                ["activityTitle"] = $"Severity: {envelope.Severity}",
                ["facts"] = facts,
            }),
        };

        if (!string.IsNullOrWhiteSpace(envelope.DeepLink))
        {
            card["potentialAction"] = new JsonArray(new JsonObject
            {
                ["@type"] = "OpenUri",
                ["name"] = "Open in IT Platform",
                ["targets"] = new JsonArray(new JsonObject
                {
                    ["os"] = "default",
                    ["uri"] = envelope.DeepLink,
                }),
            });
        }

        return card;
    }

    public static JsonNode BuildSlackPayload(NotificationEnvelope envelope)
    {
        var blocks = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "section",
                ["text"] = new JsonObject
                {
                    ["type"] = "mrkdwn",
                    ["text"] = $"*{Escape(envelope.Subject)}*\n{Escape(envelope.Body)}",
                },
            },
        };

        if (envelope.FactList.Count > 0)
        {
            var fields = new JsonArray();
            // Slack refuses a section with more than ten fields, and the extras are the least
            // important ones — truncating beats the whole message being rejected.
            foreach (var fact in envelope.FactList.Take(10))
            {
                fields.Add(new JsonObject
                {
                    ["type"] = "mrkdwn",
                    ["text"] = $"*{Escape(fact.Label)}*\n{Escape(fact.Value)}",
                });
            }

            blocks.Add(new JsonObject { ["type"] = "section", ["fields"] = fields });
        }

        if (!string.IsNullOrWhiteSpace(envelope.DeepLink))
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "context",
                ["elements"] = new JsonArray(new JsonObject
                {
                    ["type"] = "mrkdwn",
                    ["text"] = $"<{envelope.DeepLink}|Open in IT Platform>",
                }),
            });
        }

        return new JsonObject
        {
            // `text` is the notification preview and the fallback for a client that cannot render
            // blocks; Slack warns on a payload that has blocks and no text.
            ["text"] = $"[{envelope.Severity}] {envelope.Subject}",
            ["blocks"] = blocks,
        };
    }

    private static string ThemeColour(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "D93025",
        NotificationSeverity.Warning => "F9AB00",
        _ => "1A73E8",
    };

    /// <summary>Slack's mrkdwn reserves exactly these three, and only outside a link.</summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
