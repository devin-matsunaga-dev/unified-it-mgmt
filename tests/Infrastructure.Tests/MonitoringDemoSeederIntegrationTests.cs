using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;
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
    ];

    [Fact]
    public async Task Seed_WritesADeviceForEveryCiWithItsChecks()
    {
        await using var dbContext = await NewDatabaseAsync();

        var result = await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        Assert.Equal(4, result.DevicesAdded);
        var devices = await dbContext.MonitoredDevices.Include(device => device.Checks)
            .OrderBy(device => device.Id).ToListAsync();
        Assert.Equal(4, devices.Count);
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
        Assert.Equal(4, await dbContext.MonitoredDevices.CountAsync());
        Assert.Equal(4, await dbContext.ConfigChanges.CountAsync());
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
    /// Every seeded check has to survive the validator its own API applies, or the estate contains
    /// checks nobody can edit — which is exactly the WP-3.1 defect the seeded SNMP checks carry.
    /// </summary>
    [Fact]
    public async Task Seed_EveryCheck_PassesTheRulesItsOwnApiWouldApply()
    {
        await using var dbContext = await NewDatabaseAsync();
        await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        var checks = await dbContext.CheckDefinitions
            .Where(check => check.Type != CheckType.Snmp)
            .ToListAsync();

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
