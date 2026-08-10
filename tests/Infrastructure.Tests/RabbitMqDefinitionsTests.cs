using System.Text.Json;

using Contracts.Events;

using MassTransit;

using Platform.Messaging;

namespace Infrastructure.Tests;

/// <summary>
/// The document AppHost renders into the broker. Read as JSON rather than as strings, because it is
/// the shape RabbitMQ parses.
/// </summary>
public sealed class RabbitMqDefinitionsTests
{
    /// <summary>
    /// The poller cannot declare the exchange it publishes to, so the definitions file must — and it
    /// must name it exactly as MassTransit does, or the poller publishes into a void that looks
    /// healthy from both ends.
    /// </summary>
    [Fact]
    public void PollerHeartbeatExchange_MatchesTheNameMassTransitPublishesUnder()
    {
        var urn = MessageUrn.ForType<PollerHeartbeat>().ToString();

        Assert.Equal($"urn:message:{RabbitMqDefinitions.PollerHeartbeatExchange}", urn);
    }

    [Fact]
    public void Render_DeclaresTheHeartbeatExchangeAsMassTransitWould()
    {
        using var document = JsonDocument.Parse(Render());

        var exchange = Assert.Single(document.RootElement.GetProperty("exchanges").EnumerateArray());
        Assert.Equal(RabbitMqDefinitions.PollerHeartbeatExchange, exchange.GetProperty("name").GetString());
        // A type or durability mismatch fails the API's own declaration with PRECONDITION_FAILED.
        Assert.Equal("fanout", exchange.GetProperty("type").GetString());
        Assert.True(exchange.GetProperty("durable").GetBoolean());
        Assert.False(exchange.GetProperty("auto_delete").GetBoolean());
    }

    [Fact]
    public void Render_PollerPermissions_GrantWriteOnTheHeartbeatExchangeAndNothingElse()
    {
        using var document = JsonDocument.Parse(Render());

        var permission = document.RootElement.GetProperty("permissions").EnumerateArray()
            .Single(entry => entry.GetProperty("user").GetString() == "poller");

        // Empty is not "unset": in RabbitMQ an empty pattern is the regex that matches nothing, so
        // these two are the whole of "cannot declare a queue" and "cannot consume from one".
        Assert.Equal("", permission.GetProperty("configure").GetString());
        Assert.Equal("", permission.GetProperty("read").GetString());
        Assert.Equal(
            "^Contracts\\.Events:PollerHeartbeat$",
            permission.GetProperty("write").GetString());
    }

    /// <summary>
    /// The write pattern is a regular expression, so the dots in the namespace have to be escaped —
    /// unescaped they would also match, say, "ContractsXEvents:PollerHeartbeat".
    /// </summary>
    [Fact]
    public void Render_PollerWritePattern_IsAnchoredAndEscaped()
    {
        using var document = JsonDocument.Parse(Render());

        var write = document.RootElement.GetProperty("permissions").EnumerateArray()
            .Single(entry => entry.GetProperty("user").GetString() == "poller")
            .GetProperty("write").GetString()!;

        Assert.StartsWith("^", write, StringComparison.Ordinal);
        Assert.EndsWith("$", write, StringComparison.Ordinal);
        Assert.DoesNotContain("s.E", write, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WritesNoPasswordInPlaintext()
    {
        var rendered = Render();

        Assert.DoesNotContain("poller-secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret", rendered, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(rendered);
        foreach (var user in document.RootElement.GetProperty("users").EnumerateArray())
        {
            Assert.Equal("rabbit_password_hashing_sha256", user.GetProperty("hashing_algorithm").GetString());
            Assert.NotEmpty(user.GetProperty("password_hash").GetString()!);
        }
    }

    /// <summary>Two renders of one password differ, because the salt is drawn per render.</summary>
    [Fact]
    public void Render_SaltsEachPasswordHash()
    {
        var first = HashOf(Render(), "poller");
        var second = HashOf(Render(), "poller");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Render_WithNoAccounts_IsRefused() =>
        Assert.Throws<ArgumentException>(() => RabbitMqDefinitions.Render([]));

    private static string Render() => RabbitMqDefinitions.Render(
    [
        RabbitMqDefinitions.Administrator("itplatform", "admin-secret"),
        RabbitMqDefinitions.PublishOnlyPoller("poller", "poller-secret"),
    ]);

    private static string HashOf(string rendered, string username)
    {
        using var document = JsonDocument.Parse(rendered);
        return document.RootElement.GetProperty("users").EnumerateArray()
            .Single(user => user.GetProperty("name").GetString() == username)
            .GetProperty("password_hash").GetString()!;
    }
}
