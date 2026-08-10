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

    [Fact]
    public void Envelope_MessageType_IsTheUrnMassTransitRoutesOn()
    {
        using var envelope = JsonDocument.Parse(Fixture());

        var messageTypes = envelope.RootElement.GetProperty("messageType").EnumerateArray()
            .Select(type => type.GetString() ?? string.Empty).ToArray();

        Assert.Equal([MessageUrn.ForType<PollerHeartbeat>().ToString()], messageTypes);
    }

    [Fact]
    public void Envelope_EveryAddressField_IsAbsentOrAnAbsoluteUri()
    {
        using var envelope = JsonDocument.Parse(Fixture());

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
    /// The payload itself, read exactly as the consumer's serializer reads it: a property the
    /// poller renamed or dropped would arrive as a default rather than as an error.
    /// </summary>
    [Fact]
    public void Envelope_Message_DeserialisesIntoTheContractWithEveryFieldPopulated()
    {
        using var envelope = JsonDocument.Parse(Fixture());
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

    /// <summary>
    /// Named one by one rather than by count, so adding a field to the event fails here until the
    /// poller sends it — the failure mode otherwise is a silent zero on the consumer's side.
    /// </summary>
    [Fact]
    public void Envelope_Message_CarriesEveryPropertyOfTheContract()
    {
        using var envelope = JsonDocument.Parse(Fixture());
        var sent = envelope.RootElement.GetProperty("message").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var property in typeof(PollerHeartbeat).GetProperties())
        {
            Assert.True(sent.Contains(property.Name),
                $"The poller does not send '{property.Name}', so the consumer reads its default.");
        }
    }

    private static string Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ItPlatform.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
        return File.ReadAllText(Path.Combine(
            root.FullName, "services", "poller", "tests", "fixtures", "heartbeat-envelope.json"));
    }
}
