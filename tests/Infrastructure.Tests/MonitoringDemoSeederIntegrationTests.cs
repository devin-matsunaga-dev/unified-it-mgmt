using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Discovery;
using Modules.Monitoring.Features.PollerConfig;
using Modules.Monitoring.Seeding;

namespace Infrastructure.Tests;

/// <summary>
/// The seeded devices, and the one property that is not obvious about them: a monitored device
/// written without a row in <c>monitoring.config_changes</c> is invisible to every poller forever,
/// and looks from the outside exactly like a poller that is not working.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class MonitoringDemoSeederIntegrationTests(InfrastructureFixture infrastructure)
{
    private static readonly Guid[] CiIds =
    [
        Guid.Parse("0199c0de-3301-7000-8000-0000000000a1"),
        Guid.Parse("0199c0de-3301-7000-8000-0000000000a2"),
        Guid.Parse("0199c0de-3301-7000-8000-0000000000a3"),
        Guid.Parse("0199c0de-3301-7000-8000-0000000000a4"),
        Guid.Parse("0199c0de-3301-7000-8000-0000000000a5"),
        Guid.Parse("0199c0de-3301-7000-8000-0000000000a6"),
    ];

    [Fact]
    public async Task Seed_WritesADeviceForEveryCiWithItsChecks()
    {
        await using var dbContext = await NewDatabaseAsync();

        var result = await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        Assert.Equal(6, result.DevicesAdded);
        var devices = await dbContext.MonitoredDevices.Include(device => device.Checks)
            .OrderBy(device => device.Id).ToListAsync();
        Assert.Equal(6, devices.Count);
        Assert.All(devices, device => Assert.NotEmpty(device.Checks));
        Assert.Equal(result.ChecksAdded, devices.Sum(device => device.Checks.Count));
    }

    /// <summary>
    /// The estate degrades rather than fails when there are fewer CIs than devices to hang off them:
    /// a database seeded before WP-3.8 has three network CIs and no service one.
    /// </summary>
    [Fact]
    public async Task Seed_WithFewerCisThanDevices_SeedsTheOnesItCan()
    {
        await using var dbContext = await NewDatabaseAsync();

        var result = await new MonitoringDemoSeeder(dbContext)
            .SeedAsync(Plan() with { CiIds = CiIds[..3] });

        Assert.Equal(3, result.DevicesAdded);
        Assert.Empty(await dbContext.CheckDefinitions
            .Where(check => check.Type == CheckType.Tcp || check.Type == CheckType.Http)
            .ToListAsync());
    }

    /// <summary>
    /// The trap this seeder exists to avoid. Versions are allocated by the application under an
    /// advisory lock (WP-3.1), so a write that skips the config log leaves a device no poller can
    /// ever be told about — including by a full snapshot, which is built from the same log.
    /// </summary>
    [Fact]
    public async Task Seed_RecordsAConfigChangeForEveryDeviceSoAPollerCanSeeThem()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        var deviceIds = await dbContext.MonitoredDevices.Select(device => device.Id).ToListAsync();
        var changes = await dbContext.ConfigChanges.ToListAsync();

        Assert.Equal(
            deviceIds.Order(),
            changes.Where(change => change.DeviceId is not null)
                .Select(change => change.DeviceId!.Value).Distinct().Order());
        Assert.All(changes, change =>
            Assert.Equal(MonitoringConfigChangeKind.Upserted, change.Kind));
        // Versions are dense and increasing: a gap means one was allocated and rolled back.
        Assert.Equal(
            Enumerable.Range(1, changes.Count).Select(version => (long)version),
            changes.Select(change => change.Version).Order());
    }

    [Fact]
    public async Task Seed_RunTwice_AddsNothingTheSecondTime()
    {
        await using var dbContext = await NewDatabaseAsync();
        var seeder = new MonitoringDemoSeeder(dbContext);

        await seeder.SeedAsync(Plan());
        var second = await seeder.SeedAsync(Plan());

        Assert.Equal(0, second.DevicesAdded);
        Assert.Equal(6, await dbContext.MonitoredDevices.CountAsync());
        Assert.Equal(6, await dbContext.ConfigChanges.CountAsync());
    }

    /// <summary>
    /// One device is deliberately unreachable, so that "one dead device never blocks the cycle" is
    /// something an operator can watch rather than take on trust.
    /// </summary>
    [Fact]
    public async Task Seed_IncludesADeviceAtAnAddressThatCanNeverAnswer()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        var dead = await dbContext.MonitoredDevices
            .SingleAsync(device => device.Address == MonitoringDemoSeeder.UnreachableAddress);

        Assert.True(dead.IsEnabled);
        Assert.Contains("192.0.2", dead.Address, StringComparison.Ordinal);
    }

    /// <summary>
    /// WP-3.11. With the vault seeded, the community that selects the simulator's device profile is
    /// a credential id on the check rather than a string in its parameters — and the plaintext copy
    /// is <em>gone</em>, not left beside it, because a stale copy would keep polling after a
    /// rotation and make the vault look like it was working while it was being bypassed.
    /// </summary>
    [Fact]
    public async Task Seed_WithVaultedCredentials_NamesThemAndStoresNoCommunityInTheClear()
    {
        var healthy = Guid.Parse("0199c0de-3110-7000-8000-000000000001");
        var degraded = Guid.Parse("0199c0de-3110-7000-8000-000000000002");
        await using var dbContext = await NewDatabaseAsync();

        await new MonitoringDemoSeeder(dbContext).SeedAsync(
            Plan() with { HealthyCredentialId = healthy, DegradedCredentialId = degraded });

        var snmp = await dbContext.CheckDefinitions
            .Where(check => check.Type == CheckType.Snmp)
            .ToListAsync();
        Assert.NotEmpty(snmp);
        Assert.All(snmp, check =>
        {
            Assert.Contains(check.CredentialId, (Guid?[])[healthy, degraded]);
            Assert.DoesNotContain("community", check.ParametersJson, StringComparison.Ordinal);
        });
        // Both profiles are still represented, or the estate would poll one simulator device twice.
        Assert.Contains(snmp, check => check.CredentialId == healthy);
        Assert.Contains(snmp, check => check.CredentialId == degraded);

        // Nothing else authenticates, so nothing else names a credential.
        var others = await dbContext.CheckDefinitions
            .Where(check => check.Type != CheckType.Snmp)
            .ToListAsync();
        Assert.All(others, check => Assert.Null(check.CredentialId));
    }

    /// <summary>
    /// The SNMP checks have to carry the simulator's port and the community that selects its device
    /// profile, or every one of them times out against a port nothing is listening on — which is
    /// what the first live walk of this package saw, from an address the poller could not reach.
    /// <para>
    /// This is now also the WP-3.11 <em>fallback</em> path: with no credential supplied, the
    /// community stays a plaintext parameter, which is what a database seeded before that package
    /// looks like and what a check nobody has migrated keeps doing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Seed_SnmpChecks_CarryTheSimulatorsPortAndProfileCommunity()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        var parameters = await dbContext.CheckDefinitions
            .Where(check => check.Type == CheckType.Snmp)
            .Select(check => check.ParametersJson)
            .ToListAsync();
        var parsed = parameters
            .Select(json => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!)
            .ToList();

        Assert.NotEmpty(parsed);
        Assert.All(parsed, values =>
        {
            Assert.Equal("161", values["port"]);
            Assert.Equal("2c", values["version"]);
            Assert.Contains(values["community"], (string[])
                [MonitoringDemoSeeder.HealthyCommunity, MonitoringDemoSeeder.DegradedCommunity]);
        });
        Assert.Contains(parsed, values => values["metric"] == "cpu");
        Assert.Contains(parsed, values => values["metric"] == "memory");
        Assert.Contains(parsed, values => values["metric"] == "sysinfo");
        Assert.Contains(parsed, values => values["metric"] == "interfaces");
    }

    /// <summary>
    /// WP-4.5's seeded check, and the two things about it that make the WP's verification possible on
    /// a fresh run: it is on the switch whose simulator profile has an interface table, and it polls
    /// often enough that taking a port down produces an alert while somebody is still watching.
    /// </summary>
    [Fact]
    public async Task Seed_TheHealthySwitch_CarriesAnInterfaceCheckWithUtilisationThresholds()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        // Filtered in memory: `ParametersJson` is jsonb, and Postgres has no LIKE for one.
        var check = Assert.Single(
            await dbContext.CheckDefinitions.Where(candidate => candidate.Type == CheckType.Snmp).ToListAsync(),
            candidate => candidate.ParametersJson.Contains("interfaces", StringComparison.Ordinal));

        Assert.Equal(CheckType.Snmp, check.Type);
        Assert.Equal("SNMP: Interfaces", check.Name);
        // Three sustained cycles is the platform default, so this is a 90-second wait after a port
        // is shut rather than a three-minute one.
        Assert.Equal(30, check.IntervalSeconds);
        // Percent of link speed, judged per port. The busiest simulated port runs at 10% of a
        // gigabit, so a fresh run is quiet and the threshold has to be lowered to demonstrate it.
        Assert.Equal(70d, check.WarningThreshold);
        Assert.Equal(90d, check.CriticalThreshold);
        Assert.Equal(ThresholdComparison.GreaterThan, check.Comparison);
    }

    /// <summary>
    /// WP-3.8's two new check types, seeded so that "point a service check at MailHog → OK" is
    /// something a fresh <c>aspire run</c> already does rather than something to configure first.
    /// The address is the poller's route to the container, exactly as the SNMP simulator's is.
    /// </summary>
    [Fact]
    public async Task Seed_ServiceChecks_PointAtTheServiceAddressWithAPortAndAUrl()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        var device = await dbContext.MonitoredDevices.Include(device => device.Checks)
            .SingleAsync(device => device.Address == "mailhog");

        Assert.Equal("1025", Parameters(device, CheckType.Tcp)["port"]);
        Assert.Equal("http://mailhog:8025/", Parameters(device, CheckType.Http)["url"]);
        Assert.Equal("GET", Parameters(device, CheckType.Http)["method"]);
        // No ICMP check on this one, so a device whose reachability is decided entirely by service
        // checks is part of the seeded estate rather than a shape nobody has run.
        Assert.DoesNotContain(device.Checks, check => check.Type == CheckType.Icmp);
    }

    /// <summary>
    /// WP-3.12's down-able device: the one device in the estate that can be taken away on its own.
    /// It has to sit at the <em>second</em> simulator's address, because stopping the shared one
    /// takes the healthy and degraded devices with it — which is what made the Phase 3 demo
    /// impossible to perform without blacking out the rest of the board.
    /// </summary>
    [Fact]
    public async Task Seed_TheDownableDevice_PollsASimulatorOfItsOwnAndNobodyElsePollsIt()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        var devices = await dbContext.MonitoredDevices.Include(device => device.Checks).ToListAsync();
        var downable = Assert.Single(devices, device => device.Address == "snmpsim-downable");

        // Nothing else shares the container, or stopping it would take a second device too.
        Assert.Single(devices, device => device.Address == "snmpsim-downable");
        // ICMP as well as SNMP: stopping the container has to be visible as unreachable rather than
        // only as an SNMP timeout, which is what the demo points at.
        Assert.Contains(downable.Checks, check => check.Type == CheckType.Icmp);
        Assert.Contains(downable.Checks, check => check.Type == CheckType.Snmp);
        // 30s against the platform's three sustained cycles is an alert inside two minutes. At the
        // 60s the other SNMP devices use, the demo is a four-minute silence.
        Assert.All(downable.Checks, check => Assert.Equal(30, check.IntervalSeconds));
    }

    /// <summary>
    /// WP-3.12's mock HTTP target, and the only seeded check carrying a content expectation. The
    /// expectation is the point: it is a phrase in a page this repository owns, so breaking it is an
    /// edit rather than a wait for somebody else's site to change.
    /// </summary>
    [Fact]
    public async Task Seed_TheHttpTargetDevice_CarriesAContentExpectationTheMailDeviceDoesNot()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        var portal = await dbContext.MonitoredDevices.Include(device => device.Checks)
            .SingleAsync(device => device.Address == "http-target");
        var mail = await dbContext.MonitoredDevices.Include(device => device.Checks)
            .SingleAsync(device => device.Address == "mailhog");

        Assert.Equal("80", Parameters(portal, CheckType.Tcp)["port"]);
        Assert.Equal("http://http-target:80/", Parameters(portal, CheckType.Http)["url"]);
        Assert.Equal(
            "Customer portal is serving normally.",
            Parameters(portal, CheckType.Http)["expectedContent"]);
        // MailHog's page is MailHog's to change, so its check deliberately still expects nothing of
        // the body. A "simplification" that gave both the same treatment would make the mail check
        // fail on the next image bump.
        Assert.DoesNotContain("expectedContent", Parameters(mail, CheckType.Http).Keys);
    }

    /// <summary>
    /// The phrase the seeded check matches has to be in the page the mock target actually serves.
    /// They are two files that must agree and nothing else would notice if they stopped.
    /// </summary>
    [Fact]
    public void Seed_TheHttpTargetsExpectedContent_IsInThePageTheContainerServes()
    {
        var page = File.ReadAllText(RepositoryFile("src", "AppHost", "http-target", "index.html"));

        Assert.Contains(
            new MonitoringSeedPlan("snmpsim", 161, CiIds).HttpTargetExpectedContent,
            page,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The down-able simulator's data directory has to hold a profile filed under the community its
    /// checks authenticate with — snmpsim takes the community from the file name, so a rename here is
    /// a device that answers nothing and no test but this one would say why.
    /// </summary>
    [Fact]
    public void Seed_TheDownableSimulator_ServesAProfileUnderTheCommunityItsChecksUse()
    {
        var profile = RepositoryFile(
            "src", "AppHost", "snmpsim-downable", $"{MonitoringDemoSeeder.HealthyCommunity}.snmprec");

        Assert.True(File.Exists(profile), $"No simulator profile at '{profile}'.");
        // The CPU and memory OIDs its two SNMP checks read. A profile missing them answers the walk
        // with nothing, which the poller reports as a failed check rather than as a zero.
        var recording = File.ReadAllText(profile);
        Assert.Contains("1.3.6.1.2.1.25.3.3.1.2.", recording, StringComparison.Ordinal);
        Assert.Contains("1.3.6.1.2.1.25.2.3.1.6.", recording, StringComparison.Ordinal);
    }

    /// <summary>
    /// WP-4.1's two seeded scan profiles, and both of the WP's verification cases: a sweep of the
    /// scanner's own subnet, and a range guaranteed to contain nothing.
    /// </summary>
    [Fact]
    public async Task Seed_WritesTheTwoScanProfilesAFreshRunNeeds()
    {
        await using var dbContext = await NewDatabaseAsync();

        var result = await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        Assert.Equal(2, result.ScanProfilesAdded);
        var profiles = await dbContext.ScanProfiles.OrderBy(profile => profile.Name).ToListAsync();
        Assert.All(profiles, profile => Assert.True(profile.IsEnabled));
        Assert.All(profiles, profile => Assert.Equal("default", profile.DiscoveryGroup));

        // `local` rather than a CIDR, because Docker allocates the Aspire session network's subnet at
        // session start — a literal range would scan an address space nothing in the stack is on.
        var sweep = profiles.Single(profile => profile.RangesJson.Contains("local", StringComparison.Ordinal));
        Assert.True(sweep.SnmpEnabled);
        Assert.True(sweep.NeighbourDiscoveryEnabled);
        Assert.NotEmpty(JsonSerializer.Deserialize<List<int>>(sweep.PortsJson)!);

        // And a range routed nowhere, so "scan a range with nothing → clean empty result" happens on
        // every run rather than needing to be set up by hand.
        var empty = profiles.Single(profile => profile.Id != sweep.Id);
        Assert.Contains("192.0.2.", empty.RangesJson, StringComparison.Ordinal);
        Assert.False(empty.SnmpEnabled);
        Assert.Empty(JsonSerializer.Deserialize<List<int>>(empty.PortsJson)!);
    }

    /// <summary>
    /// Every seeded profile would survive the validation its own API applies, which is the guard
    /// against seeding a profile nobody can edit — the trap WP-3.1's SNMP checks fell into.
    /// </summary>
    [Fact]
    public async Task Seed_EveryScanProfile_PassesTheRulesItsOwnApiWouldApply()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        foreach (var profile in await dbContext.ScanProfiles.ToListAsync())
        {
            var errors = ScanProfileRules.Validate(
                JsonSerializer.Deserialize<List<string>>(profile.RangesJson)!,
                JsonSerializer.Deserialize<List<int>>(profile.PortsJson)!,
                profile.IntervalMinutes,
                profile.TimeoutSeconds);

            Assert.Empty(errors);
        }
    }

    /// <summary>
    /// Guarded on its own presence rather than on the devices', so a database seeded before WP-4.1 —
    /// which has devices and no scan profiles — still gets them on the next run.
    /// </summary>
    [Fact]
    public async Task Seed_AgainstADatabaseThatAlreadyHasDevices_StillAddsTheScanProfiles()
    {
        await using var dbContext = await NewDatabaseAsync();
        var seeder = new MonitoringDemoSeeder(dbContext);
        await seeder.SeedAsync(Plan());
        dbContext.ScanProfiles.RemoveRange(await dbContext.ScanProfiles.ToListAsync());
        await dbContext.SaveChangesAsync();

        var again = await seeder.SeedAsync(Plan());

        Assert.Equal(0, again.DevicesAdded);
        Assert.Equal(2, again.ScanProfilesAdded);
    }

    [Fact]
    public async Task Seed_RunTwice_AddsNoSecondCopyOfAScanProfile()
    {
        await using var dbContext = await NewDatabaseAsync();
        var seeder = new MonitoringDemoSeeder(dbContext);
        await seeder.SeedAsync(Plan());

        var again = await seeder.SeedAsync(Plan());

        Assert.Equal(0, again.ScanProfilesAdded);
        Assert.Equal(2, await dbContext.ScanProfiles.CountAsync());
    }

    /// <summary>
    /// The LLDP rows the neighbour walk reads have to be in the profile the simulator serves, and the
    /// file has to stay sorted by OID — snmpsim answers GETNEXT by walking it in order, so an unsorted
    /// record is one a walk never reaches. 1.0.8802 sorts before 1.3.6.1, which is why they come first.
    /// </summary>
    [Fact]
    public void Seed_TheHealthySimulatorProfile_CarriesTheLldpRowsANeighbourWalkReads()
    {
        var lines = File.ReadAllLines(RepositoryFile("src", "AppHost", "snmpsim", "healthy.snmprec"))
            .Where(line => line.Length > 0)
            .ToArray();

        // lldpRemSysName and lldpLocPortId: a neighbour's name and the local port that saw it.
        Assert.Contains(lines, line => line.StartsWith("1.0.8802.1.1.2.1.4.1.1.9.", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("1.0.8802.1.1.2.1.3.7.1.3.", StringComparison.Ordinal));

        var oids = lines.Select(line => line.Split('|')[0]).ToArray();
        Assert.Equal([.. oids.OrderBy(oid => oid, new OidComparer())], oids);
    }

    /// <summary>
    /// WP-4.5's own half of the same file. The interface check walks two whole tables rather than a
    /// column at a time, so what matters is that both subtrees are there — and that ifOperStatus on
    /// port 2 is writable, because a SET against it is how the WP's second verification step takes a
    /// port down without taking the device down.
    /// </summary>
    [Fact]
    public void Seed_TheHealthySimulatorProfile_CarriesTheInterfaceTableAnInterfaceCheckWalks()
    {
        var lines = File.ReadAllLines(RepositoryFile("src", "AppHost", "snmpsim", "healthy.snmprec"))
            .Where(line => line.Length > 0)
            .ToArray();

        // ifDescr and ifOperStatus from ifTable; ifHCInOctets and ifAlias from ifXTable.
        Assert.Equal(4, lines.Count(line => line.StartsWith("1.3.6.1.2.1.2.2.1.2.", StringComparison.Ordinal)));
        Assert.Equal(4, lines.Count(line => line.StartsWith("1.3.6.1.2.1.2.2.1.8.", StringComparison.Ordinal)));
        Assert.Equal(4, lines.Count(line => line.StartsWith("1.3.6.1.2.1.31.1.1.1.6.", StringComparison.Ordinal)));
        Assert.Equal(4, lines.Count(line => line.StartsWith("1.3.6.1.2.1.31.1.1.1.18.", StringComparison.Ordinal)));

        // The counters move, or every rate the poller derives is zero and the utilisation graph the
        // WP asks for is a flat line at the bottom of the chart.
        Assert.Contains(lines, line =>
            line.StartsWith("1.3.6.1.2.1.31.1.1.1.6.1|", StringComparison.Ordinal)
            && line.Contains("numeric", StringComparison.Ordinal));

        // And one port can be shut over SNMP, which is the whole verification gesture.
        Assert.Contains(lines, line =>
            line.StartsWith("1.3.6.1.2.1.2.2.1.8.2|", StringComparison.Ordinal)
            && line.Contains("writecache", StringComparison.Ordinal));
    }

    /// <summary>Numeric per sub-identifier, which is the order SNMP walks in — "10" is after "9".</summary>
    private sealed class OidComparer : IComparer<string>
    {
        public int Compare(string? left, string? right)
        {
            var first = Parse(left);
            var second = Parse(right);
            for (var index = 0; index < Math.Min(first.Length, second.Length); index++)
            {
                if (first[index] != second[index])
                {
                    return first[index].CompareTo(second[index]);
                }
            }

            return first.Length.CompareTo(second.Length);
        }

        private static long[] Parse(string? oid) =>
            [.. (oid ?? string.Empty).Split('.').Select(part => long.TryParse(part, out var value) ? value : 0)];
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ItPlatform.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
        return Path.Combine([root.FullName, .. segments]);
    }

    /// <summary>
    /// Every seeded check has to survive the validator its own API applies, or the estate contains
    /// checks nobody can edit — which is exactly the WP-3.1 defect the seeded SNMP checks carry.
    /// </summary>
    [Fact]
    public async Task Seed_EveryCheck_PassesTheRulesItsOwnApiWouldApply()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        // Every check, SNMP included. Until WP-4.5 this had to exclude them: WP-3.1's rule required
        // an `oid` parameter on any SNMP check, so the seeder's `metric=cpu` checks polled perfectly
        // and were refused by their own API. That is the assertion that would have caught it.
        var checks = await dbContext.CheckDefinitions.ToListAsync();

        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.Empty(Modules.Monitoring.Features.Devices.CheckRules.Validate(
            check.Type,
            check.IntervalSeconds,
            check.TimeoutSeconds,
            check.WarningThreshold,
            check.CriticalThreshold,
            check.Comparison,
            JsonSerializer.Deserialize<Dictionary<string, string>>(check.ParametersJson)!)));
    }

    private static IReadOnlyDictionary<string, string> Parameters(
        MonitoredDevice device,
        CheckType type) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            device.Checks.Single(check => check.Type == type).ParametersJson)!;

    /// <summary>
    /// A device is a CI plus an address. Without CIs there is nothing to monitor, and inventing ids
    /// would seed devices whose name reads null on every screen.
    /// </summary>
    [Fact]
    public async Task Seed_WithNoCis_SeedsNothingRatherThanDevicesPointingAtNothing()
    {
        await using var dbContext = await NewDatabaseAsync();

        var result = await new MonitoringDemoSeeder(dbContext)
            .SeedAsync(new MonitoringSeedPlan("snmpsim", 161, []));

        Assert.Equal(0, result.DevicesAdded);
        Assert.Empty(await dbContext.ConfigChanges.ToListAsync());
    }

    private static MonitoringSeedPlan Plan() =>
        new("snmpsim", 161, CiIds);

    /// <summary>
    /// A schema per test. The seeder is idempotent by "are there any devices", so tests sharing one
    /// database would each see the first one's rows.
    /// </summary>
    private async Task<MonitoringDbContext> NewDatabaseAsync()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(infrastructure.PostgresConnectionString)
        {
            Database = $"monitoring_seed_{Guid.NewGuid():N}",
        };

        await using (var admin = new Npgsql.NpgsqlConnection(infrastructure.PostgresConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{builder.Database}\"";
            await create.ExecuteNonQueryAsync();
        }

        var dbContext = new MonitoringDbContext(
            new DbContextOptionsBuilder<MonitoringDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options);
        await dbContext.Database.MigrateAsync();
        return dbContext;
    }
}
