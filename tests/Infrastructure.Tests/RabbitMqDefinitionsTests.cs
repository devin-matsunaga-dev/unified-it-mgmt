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
    public void DeviceTelemetryExchange_MatchesTheNameMassTransitPublishesUnder()
    {
        var urn = MessageUrn.ForType<DeviceTelemetryReported>().ToString();

        Assert.Equal($"urn:message:{RabbitMqDefinitions.DeviceTelemetryExchange}", urn);
    }

    [Fact]
    public void DeviceReachabilityExchange_MatchesTheNameMassTransitPublishesUnder()
    {
        var urn = MessageUrn.ForType<DeviceReachabilityChanged>().ToString();

        Assert.Equal($"urn:message:{RabbitMqDefinitions.DeviceReachabilityExchange}", urn);
    }

    [Fact]
    public void DeviceDiscoveredExchange_MatchesTheNameMassTransitPublishesUnder()
    {
        var urn = MessageUrn.ForType<DeviceDiscovered>().ToString();

        Assert.Equal($"urn:message:{RabbitMqDefinitions.DeviceDiscoveredExchange}", urn);
    }

    [Fact]
    public void Render_DeclaresEveryAgentExchangeAsMassTransitWould()
    {
        using var document = JsonDocument.Parse(Render());

        var exchanges = document.RootElement.GetProperty("exchanges").EnumerateArray().ToArray();

        // Both agents' lists, because neither account may declare its own exchange. Deduplicated:
        // RabbitMQ refuses a document that declares one exchange twice.
        Assert.Equal(
            RabbitMqDefinitions.DeclaredExchanges,
            [.. exchanges.Select(exchange => exchange.GetProperty("name").GetString()!)]);
        Assert.Equal(
            RabbitMqDefinitions.DeclaredExchanges.Count,
            RabbitMqDefinitions.DeclaredExchanges.Distinct(StringComparer.Ordinal).Count());
        foreach (var exchange in exchanges)
        {
            // A type or durability mismatch fails the API's own declaration with PRECONDITION_FAILED.
            Assert.Equal("fanout", exchange.GetProperty("type").GetString());
            Assert.True(exchange.GetProperty("durable").GetBoolean());
            Assert.False(exchange.GetProperty("auto_delete").GetBoolean());
        }
    }

    [Fact]
    public void Render_PollerPermissions_GrantWriteOnThePollerExchangesAndNothingElse()
    {
        using var document = JsonDocument.Parse(Render());

        var permission = document.RootElement.GetProperty("permissions").EnumerateArray()
            .Single(entry => entry.GetProperty("user").GetString() == "poller");

        // Empty is not "unset": in RabbitMQ an empty pattern is the regex that matches nothing, so
        // these two are the whole of "cannot declare a queue" and "cannot consume from one".
        Assert.Equal("", permission.GetProperty("configure").GetString());
        Assert.Equal("", permission.GetProperty("read").GetString());
        // The alternation is parenthesised. Without the group the anchors would bind to the first
        // and last branch only — "^A or B or C$" — and the middle branch would match any resource
        // whose name merely contains it.
        Assert.Equal(
            "^(Contracts\\.Events:PollerHeartbeat|Contracts\\.Events:DeviceTelemetryReported|" +
            "Contracts\\.Events:DeviceReachabilityChanged)$",
            permission.GetProperty("write").GetString());
    }

    /// <summary>
    /// The pattern names its exchanges one by one. A prefix — <c>^Contracts\.Events:.*$</c> — would
    /// read as "anything this platform publishes", which is a licence to forge a TicketCreated, and
    /// it is the obvious shortcut to reach for the next time an exchange is added here.
    /// </summary>
    [Theory]
    [InlineData("Contracts.Events:TicketCreated")]
    [InlineData("Contracts.Events:CiDeleted")]
    [InlineData("Contracts.Events:PollerHeartbeatMissed")]
    // The other agent's exchange. A poller that could publish a discovery could invent a device on
    // the network, which is exactly why the two accounts have separate lists rather than one.
    [InlineData("Contracts.Events:DeviceDiscovered")]
    public void Render_PollerWritePattern_DoesNotMatchAnotherEventsExchange(string exchange)
    {
        using var document = JsonDocument.Parse(Render());

        var write = document.RootElement.GetProperty("permissions").EnumerateArray()
            .Single(entry => entry.GetProperty("user").GetString() == "poller")
            .GetProperty("write").GetString()!;

        // The pattern carries its own anchors, so it is applied here exactly as RabbitMQ applies it.
        Assert.DoesNotMatch(write, exchange);
    }

    [Theory]
    [InlineData("Contracts.Events:PollerHeartbeat")]
    [InlineData("Contracts.Events:DeviceTelemetryReported")]
    [InlineData("Contracts.Events:DeviceReachabilityChanged")]
    public void Render_PollerWritePattern_MatchesEveryExchangeThePollerPublishesTo(string exchange)
    {
        using var document = JsonDocument.Parse(Render());

        var write = document.RootElement.GetProperty("permissions").EnumerateArray()
            .Single(entry => entry.GetProperty("user").GetString() == "poller")
            .GetProperty("write").GetString()!;

        Assert.Matches(write, exchange);
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
    public void Render_DiscoveryPermissions_GrantWriteOnItsOneExchangeAndNothingElse()
    {
        using var document = JsonDocument.Parse(Render());

        var permission = document.RootElement.GetProperty("permissions").EnumerateArray()
            .Single(entry => entry.GetProperty("user").GetString() == "discovery");

        Assert.Equal("", permission.GetProperty("configure").GetString());
        Assert.Equal("", permission.GetProperty("read").GetString());
        Assert.Equal(
            "^(Contracts\\.Events:DeviceDiscovered)$",
            permission.GetProperty("write").GetString());
    }

    /// <summary>
    /// The separation in the direction that matters most for the vault: a scanner that could publish
    /// telemetry could report a measurement of a device it has never polled, and a heartbeat would let
    /// it impersonate a poller outright.
    /// </summary>
    [Theory]
    [InlineData("Contracts.Events:PollerHeartbeat")]
    [InlineData("Contracts.Events:DeviceTelemetryReported")]
    [InlineData("Contracts.Events:DeviceReachabilityChanged")]
    [InlineData("Contracts.Events:TicketCreated")]
    public void Render_DiscoveryWritePattern_DoesNotMatchAnotherAgentsExchange(string exchange)
    {
        using var document = JsonDocument.Parse(Render());

        var write = document.RootElement.GetProperty("permissions").EnumerateArray()
            .Single(entry => entry.GetProperty("user").GetString() == "discovery")
            .GetProperty("write").GetString()!;

        Assert.DoesNotMatch(write, exchange);
    }

    [Fact]
    public void Render_DiscoveryWritePattern_MatchesTheExchangeItPublishesTo()
    {
        using var document = JsonDocument.Parse(Render());

        var write = document.RootElement.GetProperty("permissions").EnumerateArray()
            .Single(entry => entry.GetProperty("user").GetString() == "discovery")
            .GetProperty("write").GetString()!;

        Assert.Matches(write, "Contracts.Events:DeviceDiscovered");
    }

    [Fact]
    public void Render_WritesNoPasswordInPlaintext()
    {
        var rendered = Render();

        Assert.DoesNotContain("poller-secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("discovery-secret", rendered, StringComparison.Ordinal);
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
        RabbitMqDefinitions.PublishOnlyDiscovery("discovery", "discovery-secret"),
    ]);

    private static string HashOf(string rendered, string username)
    {
        using var document = JsonDocument.Parse(rendered);
        return document.RootElement.GetProperty("users").EnumerateArray()
            .Single(user => user.GetProperty("name").GetString() == username)
            .GetProperty("password_hash").GetString()!;
    }
}
