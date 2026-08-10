using System.Text.Json;

using Contracts.Events;

using MassTransit;
using MassTransit.Serialization;

namespace Infrastructure.Tests;

/// <summary>
/// The contract between the two languages. The Python poller does not use MassTransit — it
/// hand-builds the JSON envelope — so `services/poller/tests/fixtures/heartbeat-envelope.json` is a
/// committed sample of exactly what `poller.bus.build_envelope` emits, asserted on the Python side
/// by `test_bus.py` and read here with MassTransit's own serializer settings.
/// <para>
/// This exists because the first live run of the poller dead-lettered every heartbeat with
/// <c>System.UriFormatException: Invalid URI: Invalid port specified</c>. The envelope carried
/// <c>destinationAddress: "exchange://Contracts.Events:PollerHeartbeat"</c>, and MassTransit parses
/// the address fields as absolute URIs in <c>EnvelopeMessageContext.ConvertToUri</c> during
/// deserialisation, before any consumer runs — so the exchange name's colon read as a port. Both
/// sides had passing tests and neither had ever read the other's output.
/// </para>
/// <para>
/// These assertions are deliberately infrastructure-free and deterministic. That a heartbeat
/// travels the broker into the real consumer is covered by
/// <see cref="PollerHeartbeatBusIntegrationTests"/>, and end to end by the live `aspire run` walk.
/// </para>
/// </summary>
public sealed class PollerEnvelopeTests
{
    /// <summary>
    /// Every envelope field MassTransit turns into a <see cref="Uri"/> while deserialising. Each
    /// must be absent or an absolute URI; anything else faults the message before a consumer sees it.
    /// </summary>
    private static readonly string[] AddressFields =
        ["sourceAddress", "destinationAddress", "responseAddress", "faultAddress"];

    /// <summary>Every envelope the poller publishes, and the event each one carries.</summary>
    public static TheoryData<string, Type> Envelopes() => new()
    {
        { "heartbeat-envelope.json", typeof(PollerHeartbeat) },
        { "telemetry-envelope.json", typeof(DeviceTelemetryReported) },
        { "reachability-envelope.json", typeof(DeviceReachabilityChanged) },
    };

    [Theory]
    [MemberData(nameof(Envelopes))]
    public void Envelope_MessageType_IsTheUrnMassTransitRoutesOn(string fixture, Type contract)
    {
        using var envelope = JsonDocument.Parse(Fixture(fixture));

        var messageTypes = envelope.RootElement.GetProperty("messageType").EnumerateArray()
            .Select(type => type.GetString() ?? string.Empty).ToArray();

        Assert.Equal([MessageUrn.ForType(contract).ToString()], messageTypes);
    }

    [Theory]
    [MemberData(nameof(Envelopes))]
    public void Envelope_EveryAddressField_IsAbsentOrAnAbsoluteUri(string fixture, Type contract)
    {
        _ = contract;
        using var envelope = JsonDocument.Parse(Fixture(fixture));

        foreach (var field in AddressFields)
        {
            if (!envelope.RootElement.TryGetProperty(field, out var value))
            {
                continue;
            }

            Assert.True(
                Uri.TryCreate(value.GetString(), UriKind.Absolute, out _),
                $"'{field}' is '{value.GetString()}', which MassTransit cannot parse as a URI. " +
                "It will fault every message during deserialisation.");
        }
    }

    /// <summary>
    /// Named one by one rather than by count, for every event the poller sends: a property the
    /// poller renamed or never sends arrives as a default, which on a metric reads as a zero.
    /// </summary>
    [Theory]
    [MemberData(nameof(Envelopes))]
    public void Envelope_Message_CarriesEveryPropertyOfItsContract(string fixture, Type contract)
    {
        using var envelope = JsonDocument.Parse(Fixture(fixture));
        var sent = envelope.RootElement.GetProperty("message").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var property in contract.GetProperties())
        {
            Assert.True(sent.Contains(property.Name),
                $"The poller does not send '{property.Name}', so the consumer reads its default.");
        }
    }

    /// <summary>
    /// The telemetry payload, read exactly as a consumer's serializer reads it — including the two
    /// nested levels, which is where a hand-built envelope is most likely to diverge.
    /// </summary>
    [Fact]
    public void TelemetryEnvelope_Message_DeserialisesWithItsResultsAndMetrics()
    {
        using var envelope = JsonDocument.Parse(Fixture("telemetry-envelope.json"));
        var body = envelope.RootElement.GetProperty("message").GetRawText();

        var telemetry = JsonSerializer.Deserialize<DeviceTelemetryReported>(
            body, SystemTextJsonMessageSerializer.Options);

        Assert.NotNull(telemetry);
        Assert.Equal("POLLER_NAME_PLACEHOLDER", telemetry.PollerName);
        Assert.Equal(7, telemetry.CycleNumber);
        Assert.Equal(2, telemetry.Results.Count);

        var measured = telemetry.Results[0];
        Assert.True(measured.Succeeded);
        Assert.Equal("Icmp", measured.CheckType);
        Assert.Equal(1.42, measured.LatencyMs);
        Assert.Null(measured.Error);
        var metric = measured.Metrics[0];
        Assert.Equal("icmp.rtt_ms", metric.Name);
        Assert.Equal(1.42, metric.Value);
        Assert.Equal("ms", metric.Unit);
        // A number and a name are different things, and WP-3.4 tells them apart by which is null.
        Assert.Null(metric.Text);

        // A check that failed still travels: a timeout is a fact about the device.
        var failed = telemetry.Results[1];
        Assert.False(failed.Succeeded);
        Assert.NotNull(failed.Error);
        Assert.Empty(failed.Metrics);
    }

    [Fact]
    public void ReachabilityEnvelope_Message_DeserialisesIntoTheContract()
    {
        using var envelope = JsonDocument.Parse(Fixture("reachability-envelope.json"));
        var body = envelope.RootElement.GetProperty("message").GetRawText();

        var change = JsonSerializer.Deserialize<DeviceReachabilityChanged>(
            body, SystemTextJsonMessageSerializer.Options);

        Assert.NotNull(change);
        Assert.NotEqual(Guid.Empty, change.DeviceId);
        Assert.NotEqual(Guid.Empty, change.CiId);
        Assert.False(change.IsReachable);
        Assert.Equal(2, change.ConsecutiveFailures);
        Assert.Equal("10.10.20.31", change.Address);
        Assert.NotNull(change.Error);
    }

    /// <summary>
    /// The payload itself, read exactly as the consumer's serializer reads it: a property the
    /// poller renamed or dropped would arrive as a default rather than as an error.
    /// </summary>
    [Fact]
    public void Envelope_Message_DeserialisesIntoTheContractWithEveryFieldPopulated()
    {
        using var envelope = JsonDocument.Parse(Fixture("heartbeat-envelope.json"));
        var body = envelope.RootElement.GetProperty("message").GetRawText();

        var heartbeat = JsonSerializer.Deserialize<PollerHeartbeat>(
            body, SystemTextJsonMessageSerializer.Options);

        Assert.NotNull(heartbeat);
        Assert.NotEqual(Guid.Empty, heartbeat.EventId);
        Assert.NotEqual(default, heartbeat.OccurredAt);
        Assert.Equal("POLLER_NAME_PLACEHOLDER", heartbeat.PollerName);
        Assert.Equal("default", heartbeat.PollerGroup);
        Assert.Equal("0.1.0", heartbeat.AgentVersion);
        Assert.Equal(12, heartbeat.ConfigVersion);
        Assert.Equal(15, heartbeat.IntervalSeconds);
        Assert.Equal(4, heartbeat.DeviceCount);
        Assert.Equal(3, heartbeat.CycleNumber);
    }

    private static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ItPlatform.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
        return File.ReadAllText(Path.Combine(
            root.FullName, "services", "poller", "tests", "fixtures", name));
    }
}
