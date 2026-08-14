using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;

namespace Modules.Assets.Seeding;

public sealed record DiscoveryFactsSeedResult(int FactsAdded);

/// <summary>
/// What a scan would have observed about the seeded estate, so that the drift report has something
/// true to say on a clean-slate database.
/// <para>
/// The dev database is recreated on most AppHost restarts and a live scan of the Aspire session
/// network finds Docker addresses rather than the estate's <c>10.10.0.x</c> ones, so without this the
/// report opens empty on every fresh run — correct, and indistinguishable from a broken feature. Each
/// row below is placed to put one finding on the first screen, the same rule WP-2.8 set for the estate
/// and WP-4.4 for the software catalogue.
/// </para>
/// <para>
/// These are <em>observations</em>, not assertions: they go into <c>assets.ci_discovery_facts</c>,
/// which is exactly where a real sighting lands, and a later scan that matches one of these CIs
/// overwrites the row it seeded. Nothing here touches a CI's own attributes — that is the split the
/// whole feature rests on.
/// </para>
/// </summary>
public sealed class DiscoveryFactsSeeder(AssetsDbContext dbContext)
{
    private const string DiscoveryName = "seed-scanner";
    private const string ScanProfileName = "Seeded estate observation";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One observation, named by <see cref="CiSeed.Key"/>. <paramref name="LastSeenDaysAgo"/> is an
    /// offset rather than a date because a fixed one drifts into the past on a database that is
    /// recreated weekly — the same call WP-2.8 made for contract dates.
    /// </summary>
    private sealed record FactsSeed(
        string CiKey,
        string Address,
        string? SysName,
        string? SysLocation,
        string SysDescription,
        int LastSeenDaysAgo = 0,
        IReadOnlyList<NeighbourSeed>? Neighbours = null,
        IReadOnlyList<int>? OpenPorts = null);

    private sealed record NeighbourSeed(string RemoteSystemName, string LocalPort, string RemotePort);

    /// <summary>
    /// The fixture. Between them these seven rows put a Changed finding on three different fields, both
    /// shapes of Missing, and one cable nobody recorded — which is every kind of finding the report can
    /// produce except a per-field <c>New</c>, and that one needs a CI with a blank field, which the
    /// seeded estate deliberately does not contain. Clearing a CI's site in the edit form produces it.
    /// </summary>
    private static readonly IReadOnlyList<FactsSeed> Facts =
    [
        // Agrees on everything, and is the only reporter of neighbours. Its uplink to the router is a
        // relationship the estate already records, so it confirms an asserted edge; its link to core
        // switch B is not recorded anywhere, which makes it the cable somebody patched and nobody wrote
        // down — the finding WP-4.3 deliberately declined to write into `ci_relationships`.
        new("dc1-core-sw-01", "10.10.0.2", "dc1-core-sw-01", "Primary Data Centre",
            "Cisco IOS Software, Catalyst L3 Switch Software, Version 17.9.4",
            Neighbours:
            [
                new("dc1-core-rtr-01", "GigabitEthernet0/1", "GigabitEthernet0/24"),
                new("dc1-core-sw-02", "GigabitEthernet0/2", "GigabitEthernet0/2"),
            ],
            OpenPorts: [22, 443]),

        // Answered SNMP and left sysLocation empty while the CMDB records a site: a device somebody
        // never finished configuring. Missing rather than Changed — the network is not contradicting
        // the record, it simply has nothing to say.
        new("dc1-core-rtr-01", "10.10.0.1", "dc1-core-rtr-01", null,
            "Cisco IOS Software, ISR Software, Version 17.9.4", OpenPorts: [22, 443]),

        new("dc1-core-sw-02", "10.10.0.3", "dc1-core-sw-02", "Primary Data Centre",
            "Cisco IOS Software, Catalyst L3 Switch Software, Version 17.9.4", OpenPorts: [22, 443]),

        // Racked in the data centre; the CMDB still says Head Office. The WP's own verification case,
        // pre-baked so the report is not empty before anybody edits anything.
        new("hq-acc-sw-01", "10.20.0.2", "hq-acc-sw-01", "Primary Data Centre",
            "ArubaOS-CX Version 10.13", OpenPorts: [22, 443]),

        // Re-addressed during a subnet change. The CMDB's management IP is 10.20.0.3 and it answered on
        // .53, which is the one field somebody will try to poll and fail.
        new("hq-acc-sw-02", "10.20.0.53", "hq-acc-sw-02", "Head Office",
            "ArubaOS-CX Version 10.13", OpenPorts: [22, 443]),

        // Rebuilt and renamed, and nobody told the CMDB. The recorded hostname is dc1-esx-01.
        new("dc1-esx-01", "10.10.1.11", "dc1-esx-99", "Primary Data Centre",
            "VMware ESXi 8.0.2 build-23305546", OpenPorts: [22, 443, 902]),

        // Has not answered a scan in a month. The other shape of missing, and the one an asset manager
        // means by the word: the record is still here and the thing it describes has stopped answering.
        new("br1-sw-01", "10.30.0.2", "br1-sw-01", "Regional Branch",
            "Cisco IOS Software, Catalyst L2 Switch Software, Version 17.6.5",
            LastSeenDaysAgo: 32, OpenPorts: [22]),
    ];

    /// <summary>
    /// Guarded on the CI rather than on a deterministic id: <c>ci_discovery_facts</c> is keyed by CI, so
    /// a row a real scan has already written is left exactly as the scan left it. A seeder that is meant
    /// to be re-runnable must not assume it is the only writer — the rule WP-4.4's seeder had to learn.
    /// </summary>
    public async Task<DiscoveryFactsSeedResult> SeedAsync(
        IReadOnlyDictionary<string, Guid> ciIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciIds);

        var now = DateTimeOffset.UtcNow;
        var wanted = Facts
            .Where(fact => ciIds.ContainsKey(fact.CiKey))
            .Select(fact => (Fact: fact, CiId: ciIds[fact.CiKey]))
            .ToList();
        if (wanted.Count == 0)
        {
            return new DiscoveryFactsSeedResult(0);
        }

        var candidateIds = wanted.Select(entry => entry.CiId).ToList();
        var existing = await dbContext.CiDiscoveryFacts
            .Where(facts => candidateIds.Contains(facts.CiId))
            .Select(facts => facts.CiId)
            .ToHashSetAsync(cancellationToken);

        var added = 0;
        foreach (var (fact, ciId) in wanted)
        {
            if (existing.Contains(ciId))
            {
                continue;
            }

            var lastSeen = now.AddDays(-fact.LastSeenDaysAgo);
            dbContext.CiDiscoveryFacts.Add(new CiDiscoveryFacts
            {
                CiId = ciId,
                Address = fact.Address,
                Hostname = fact.SysName is null ? null : $"{fact.SysName}.corp.local",
                RespondedToPing = true,
                OpenPortsJson = JsonSerializer.Serialize(fact.OpenPorts ?? [], Json),
                SysName = fact.SysName,
                SysDescription = fact.SysDescription,
                SysObjectId = "1.3.6.1.4.1.9.1.2494",
                SysLocation = fact.SysLocation,
                SysContact = "itops@example.com",
                UptimeSeconds = 86_400 * (7 + fact.LastSeenDaysAgo),
                NeighboursJson = JsonSerializer.Serialize(
                    (fact.Neighbours ?? []).Select(neighbour => new
                    {
                        protocol = "lldp",
                        localPort = neighbour.LocalPort,
                        remoteSystemName = neighbour.RemoteSystemName,
                        remotePort = neighbour.RemotePort,
                        remoteAddress = (string?)null,
                    }),
                    Json),
                DiscoveryName = DiscoveryName,
                ScanProfileName = ScanProfileName,
                LastScanId = Guid.CreateVersion7(),
                // First seen well before last seen, so the sighting counters read like a device that has
                // been on the network for months rather than one found this morning.
                FirstSeenAt = lastSeen.AddDays(-90),
                LastSeenAt = lastSeen,
                SightingCount = 90 * 24 * 12,
            });
            added++;
        }

        if (added > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new DiscoveryFactsSeedResult(added);
    }

    /// <summary>The keys this seeder observes, so a test can assert the fixture without a database.</summary>
    public static IReadOnlyList<string> ObservedCiKeys => [.. Facts.Select(fact => fact.CiKey)];

    /// <summary>
    /// The fixture as the drift comparator sees it, for the unit test that pins what a fresh run shows.
    /// Recorded values are the caller's to supply — they come from <see cref="AssetsEstate"/>, and the
    /// point of the test is that these two hand-written tables still disagree in the ways they were
    /// written to disagree.
    /// </summary>
    public static IReadOnlyList<(string CiKey, string Address, string? SysName, string? SysLocation, int LastSeenDaysAgo)>
        ObservedValues =>
        [.. Facts.Select(fact => (fact.CiKey, fact.Address, fact.SysName, fact.SysLocation, fact.LastSeenDaysAgo))];
}
