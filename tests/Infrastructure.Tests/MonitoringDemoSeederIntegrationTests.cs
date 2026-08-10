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
    ];

    [Fact]
    public async Task Seed_WritesThreeDevicesWithTheirChecks()
    {
        await using var dbContext = await NewDatabaseAsync();

        var result = await new MonitoringDemoSeeder(dbContext).SeedAsync(Plan());

        Assert.Equal(3, result.DevicesAdded);
        var devices = await dbContext.MonitoredDevices.Include(device => device.Checks)
            .OrderBy(device => device.Id).ToListAsync();
        Assert.Equal(3, devices.Count);
        Assert.All(devices, device => Assert.NotEmpty(device.Checks));
        Assert.Equal(result.ChecksAdded, devices.Sum(device => device.Checks.Count));
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
        Assert.Equal(3, await dbContext.MonitoredDevices.CountAsync());
        Assert.Equal(3, await dbContext.ConfigChanges.CountAsync());
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
    /// The SNMP checks have to carry the simulator's port and the community that selects its device
    /// profile, or every one of them times out against a port nothing is listening on — which is
    /// what the first live walk of this package saw, from an address the poller could not reach.
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
