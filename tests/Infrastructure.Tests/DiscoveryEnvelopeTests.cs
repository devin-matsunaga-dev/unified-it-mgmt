using System.Text.Json;

using Contracts.Events;

using MassTransit;
using MassTransit.Serialization;

namespace Infrastructure.Tests;

/// <summary>
/// The contract between the two languages, for the discovery service's one event.
/// <para>
/// The Python scanner does not use MassTransit — it hand-builds the JSON envelope — so
/// <c>services/discovery/tests/fixtures/discovered-envelope.json</c> is a committed sample of exactly
/// what <c>discovery.bus.build_envelope</c> emits, asserted on the Python side by <c>test_bus.py</c>
/// and read here with MassTransit's own serializer settings.
/// </para>
/// <para>
/// It exists because of what happened to the poller in WP-3.2: the first live run dead-lettered every
/// heartbeat with <c>Invalid URI: Invalid port specified</c>, because MassTransit parses the envelope's
/// address fields as absolute URIs during deserialisation and the exchange name contains a colon. Both
/// sides had passing tests and neither had ever read the other's output. <see cref="PollerEnvelopeTests"/>
/// is the same guard for the poller's three events.
/// </para>
/// <para>
/// <c>DeviceDiscovered</c> has no consumer yet — WP-4.2 owns the review queue — which makes this guard
/// more valuable rather than less: it is the only thing standing between a scanner publishing happily
/// today and a consumer silently reading defaults tomorrow.
/// </para>
/// </summary>
public sealed class DiscoveryEnvelopeTests
{
    /// <summary>
    /// Every envelope field MassTransit turns into a <see cref="Uri"/> while deserialising. Each must
    /// be absent or an absolute URI; anything else faults the message before a consumer sees it.
    /// </summary>
    private static readonly string[] AddressFields =
        ["sourceAddress", "destinationAddress", "responseAddress", "faultAddress"];

    [Fact]
    public void Envelope_MessageType_IsTheUrnMassTransitRoutesOn()
    {
        using var envelope = JsonDocument.Parse(Fixture());

        var messageTypes = envelope.RootElement.GetProperty("messageType").EnumerateArray()
            .Select(type => type.GetString() ?? string.Empty).ToArray();

        Assert.Equal([MessageUrn.ForType(typeof(DeviceDiscovered)).ToString()], messageTypes);
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
    /// Named property by property rather than by count, at all three levels of the payload. A property
    /// the scanner renamed or never sends arrives as a default — false on a boolean, empty on a list,
    /// null on a string — which is a silent wrong answer rather than an error.
    /// </summary>
    [Theory]
    [InlineData(typeof(DeviceDiscovered), null)]
    [InlineData(typeof(DiscoveredSnmpIdentity), "snmp")]
    [InlineData(typeof(DiscoveredNeighbour), "neighbours")]
    public void Envelope_Message_CarriesEveryPropertyOfItsContract(Type contract, string? nested)
    {
        using var envelope = JsonDocument.Parse(Fixture());
        var message = envelope.RootElement.GetProperty("message");
        var subject = nested switch
        {
            null => message,
            "neighbours" => message.GetProperty("neighbours").EnumerateArray().First(),
            var name => message.GetProperty(name),
        };

        var sent = subject.EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var property in contract.GetProperties())
        {
            Assert.True(sent.Contains(property.Name),
                $"The scanner does not send '{property.Name}', so the consumer reads its default.");
        }
    }

    [Fact]
    public void Envelope_Message_DeserialisesWithItsIdentityAndNeighbours()
    {
        using var envelope = JsonDocument.Parse(Fixture());
        var body = envelope.RootElement.GetProperty("message").GetRawText();

        var discovered = JsonSerializer.Deserialize<DeviceDiscovered>(
            body, SystemTextJsonMessageSerializer.Options);

        Assert.NotNull(discovered);
        Assert.NotEqual(Guid.Empty, discovered.EventId);
        Assert.NotEqual(default, discovered.OccurredAt);
        Assert.Equal("DISCOVERY_NAME_PLACEHOLDER", discovered.DiscoveryName);
        Assert.NotEqual(Guid.Empty, discovered.ScanProfileId);
        Assert.Equal("Local subnet sweep", discovered.ScanProfileName);
        Assert.NotEqual(Guid.Empty, discovered.ScanId);
        Assert.Equal("172.18.0.7", discovered.Address);
        Assert.Equal("sim-switch-healthy.example.test", discovered.Hostname);
        // Which protocol named it. A field the publisher omits arrives here as null, which would
        // read as "nothing named it" on a device reverse DNS answered for.
        Assert.Equal("dns", discovered.HostnameSource);
        Assert.True(discovered.RespondedToPing);
        Assert.Equal([22, 161], discovered.OpenPorts);

        Assert.NotNull(discovered.Snmp);
        Assert.Equal("sim-switch-healthy", discovered.Snmp.SysName);
        Assert.Equal("1.3.6.1.4.1.8072.3.2.10", discovered.Snmp.SysObjectId);
        Assert.Equal("Primary Data Centre", discovered.Snmp.SysLocation);
        // sysUpTime arrives in seconds. The scanner divides by the timeticks-per-second constant, so a
        // consumer never has to know which MIB the number came from.
        Assert.Equal(5_184_000, discovered.Snmp.UptimeSeconds);

        // Both protocols, additively: a switch with LLDP and CDP both on reports the same link twice,
        // and reconciling them into one edge is WP-4.3's.
        Assert.Equal(2, discovered.Neighbours.Count);
        Assert.Equal("lldp", discovered.Neighbours[0].Protocol);
        Assert.Equal("GigabitEthernet0/1", discovered.Neighbours[0].LocalPort);
        Assert.Equal("dc1-core-rtr-01", discovered.Neighbours[0].RemoteSystemName);
        // LLDP advertises no management address without a second indexed table, so this is null and
        // that is the shape a consumer has to handle.
        Assert.Null(discovered.Neighbours[0].RemoteAddress);
        Assert.Equal("cdp", discovered.Neighbours[1].Protocol);
        Assert.Equal("172.18.0.1", discovered.Neighbours[1].RemoteAddress);
    }

    /// <summary>
    /// The community a device answered on is not on this event, and this is the assertion that keeps it
    /// that way. It is the one thing a scan learns that is a secret in a real estate, and the event
    /// travels the bus and lands in a review queue somebody reads (ARCHITECTURE §7.3).
    /// </summary>
    [Fact]
    public void Envelope_Message_CarriesNoCommunityString()
    {
        // Both halves matter. The fixture is what the scanner sends today; the contract is what it
        // could send tomorrow, and a `Community` property added to the record would make the next
        // scanner fill it in.
        Assert.DoesNotContain("community", Fixture(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(DiscoveredSnmpIdentity).GetProperties(),
            property => property.Name.Contains("community", StringComparison.OrdinalIgnoreCase));
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
            root.FullName, "services", "discovery", "tests", "fixtures", "discovered-envelope.json"));
    }
}
