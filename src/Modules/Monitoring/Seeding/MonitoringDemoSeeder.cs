using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.PollerConfig;

namespace Modules.Monitoring.Seeding;

/// <summary>
/// Where the seeded devices point and what they answer with. Supplied by the caller because the
/// simulator's address is an AppHost fact, not a monitoring one.
/// </summary>
/// <param name="SnmpAddress">Host the SNMP simulator answers on, as the poller's container reaches it.</param>
/// <param name="SnmpPort">Port it answers on; not 161, because binding that needs privileges.</param>
/// <param name="CiIds">
/// Which CIs to monitor, taken as an argument because Monitoring may not reference Assets — the same
/// route WP-2.8's ticket↔CI links took. Four are used; fewer seeds fewer devices.
/// </param>
/// <param name="PollerGroup">Which poller owns these devices. Must match the running poller's group.</param>
/// <param name="ServiceAddress">
/// Host the seeded service checks point at, again as the poller's container reaches it. MailHog under
/// <c>aspire run</c>: it is the one resource in the stack that answers both a TCP port and an HTTP
/// request, which is exactly what WP-3.8's two new check types need to demonstrate.
/// </param>
/// <param name="ServiceTcpPort">A port the service accepts a connection on (MailHog's SMTP listener).</param>
/// <param name="ServiceHttpUrl">A URL the service answers 200 on (MailHog's UI).</param>
public sealed record MonitoringSeedPlan(
    string SnmpAddress,
    int SnmpPort,
    IReadOnlyList<Guid> CiIds,
    string PollerGroup = "default",
    string ServiceAddress = "mailhog",
    int ServiceTcpPort = 1025,
    string ServiceHttpUrl = "http://mailhog:8025/");

public sealed record MonitoringSeedResult(int DevicesAdded, int ChecksAdded);

/// <summary>
/// Three monitored devices, so a fresh <c>aspire run</c> has something to poll.
/// <para>
/// Unlike the Assets estate this writes through the DbContext <em>and</em> the config log, because
/// they are not separable: a device written without a
/// <see cref="IMonitoringConfigLog.RecordAsync"/> in the same transaction is invisible to every
/// poller forever, which looks exactly like a broken poller. WP-3.1's versions are allocated by the
/// application under an advisory lock, so the whole seed runs in one transaction.
/// </para>
/// <para>
/// It is deliberately not audited and publishes nothing, following the WP-2.8 rule that seeded rows
/// are reference data rather than operator actions.
/// </para>
/// </summary>
public sealed class MonitoringDemoSeeder(MonitoringDbContext dbContext)
{
    private const string Actor = "seeder";

    /// <summary>
    /// TEST-NET-1 (RFC 5737), reserved for documentation and routed nowhere. It is the one address
    /// guaranteed never to answer, which is what makes "one dead device never blocks the cycle"
    /// demonstrable without unplugging anything.
    /// </summary>
    public const string UnreachableAddress = "192.0.2.1";

    /// <summary>The simulator profile each SNMP device reads, as a community string.</summary>
    public const string HealthyCommunity = "healthy";
    public const string DegradedCommunity = "degraded";

    public async Task<MonitoringSeedResult> SeedAsync(
        MonitoringSeedPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (await dbContext.MonitoredDevices.AnyAsync(cancellationToken))
        {
            // Idempotent by presence, like every other seeder here: a re-run against a database that
            // already has devices must add nothing rather than a second copy of each.
            return new MonitoringSeedResult(0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var configLog = new MonitoringConfigLog(dbContext);
        var devices = Plan(plan, now).ToList();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var device in devices)
        {
            dbContext.MonitoredDevices.Add(device);
            // One change per device, not per check: the delta's unit is the whole device (WP-3.1).
            await configLog.RecordAsync(
                MonitoringConfigEntity.Device,
                device.Id,
                device.Id,
                device.PollerGroup,
                MonitoringConfigChangeKind.Upserted,
                cancellationToken);
            // Saved per device rather than once at the end, because `RecordAsync` allocates its
            // version from `max(version)` *in the database*: two unsaved calls in one transaction
            // both compute the same number and collide on the primary key. The advisory lock is
            // transaction-scoped and still held across all of these, so no other writer interleaves.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new MonitoringSeedResult(devices.Count, devices.Sum(device => device.Checks.Count));
    }

    private static IEnumerable<MonitoredDevice> Plan(MonitoringSeedPlan plan, DateTimeOffset now)
    {
        // A device is a CI plus an address, so a seeded device needs a CI that exists — otherwise
        // its name reads null on every screen and in the poller's own configuration.
        if (plan.CiIds.Count == 0)
        {
            yield break;
        }

        // A healthy device: pings, and answers SNMP with quiet numbers.
        yield return Device(
            Guid.Parse("0199c0de-3300-7000-8000-000000000001"),
            plan.CiIds[0],
            plan,
            now,
            "Simulated switch — healthy profile",
            plan.SnmpAddress,
            [
                Ping(now, "Reachability", intervalSeconds: 30),
                Snmp(now, "System information", "sysinfo", plan, HealthyCommunity, 300),
                Snmp(now, "CPU", "cpu", plan, HealthyCommunity, 60, warning: 70, critical: 90),
                Snmp(now, "Memory", "memory", plan, HealthyCommunity, 60, warning: 80, critical: 95),
            ]);

        // The same simulator, read through a community that reports a device under strain. Nothing
        // alerts on it yet — WP-3.5 owns that — but the numbers are there to alert on.
        if (plan.CiIds.Count < 2)
        {
            yield break;
        }

        yield return Device(
            Guid.Parse("0199c0de-3300-7000-8000-000000000002"),
            plan.CiIds[1],
            plan,
            now,
            "Simulated server — degraded profile",
            plan.SnmpAddress,
            [
                Ping(now, "Reachability", intervalSeconds: 30),
                Snmp(now, "CPU", "cpu", plan, DegradedCommunity, 60, warning: 70, critical: 90),
                Snmp(now, "Memory", "memory", plan, DegradedCommunity, 60, warning: 80, critical: 95),
            ]);

        // The dead one. It exists so that "the other devices keep polling" is something an operator
        // can watch rather than take on trust.
        if (plan.CiIds.Count < 3)
        {
            yield break;
        }

        yield return Device(
            Guid.Parse("0199c0de-3300-7000-8000-000000000003"),
            plan.CiIds[2],
            plan,
            now,
            "Unreachable by design (RFC 5737 documentation address)",
            UnreachableAddress,
            [Ping(now, "Reachability", intervalSeconds: 30, timeoutSeconds: 3)]);

        // A service rather than a box: WP-3.8's checks answer "is this listener accepting" and "is
        // this site serving the page it should", neither of which an SNMP agent is asked. It carries
        // no ICMP check on purpose, so that a device whose reachability is decided by service checks
        // alone is part of the seeded estate rather than a shape nobody has run.
        // No TLS check is seeded: nothing in the dev stack serves HTTPS, and a seeded check pointing
        // at a public host would raise a permanent alert on a machine with no internet.
        if (plan.CiIds.Count < 4)
        {
            yield break;
        }

        yield return Device(
            Guid.Parse("0199c0de-3300-7000-8000-000000000004"),
            plan.CiIds[3],
            plan,
            now,
            "Mail service — TCP and HTTP service checks",
            plan.ServiceAddress,
            [
                Tcp(now, "SMTP port", plan.ServiceTcpPort, intervalSeconds: 30),
                Http(now, "Web UI", plan.ServiceHttpUrl, intervalSeconds: 30),
            ]);
    }

    private static MonitoredDevice Device(
        Guid id,
        Guid ciId,
        MonitoringSeedPlan plan,
        DateTimeOffset now,
        string notes,
        string address,
        List<CheckDefinition> checks) => new()
        {
            Id = id,
            CiId = ciId,
            Address = address,
            PollerGroup = plan.PollerGroup,
            IsEnabled = true,
            Notes = notes,
            CreatedBy = Actor,
            CreatedAt = now,
            UpdatedBy = Actor,
            UpdatedAt = now,
            Checks = checks,
        };

    private static CheckDefinition Ping(
        DateTimeOffset now,
        string name,
        int intervalSeconds,
        int timeoutSeconds = 5) => Check(now, CheckType.Icmp, name, intervalSeconds, timeoutSeconds,
            new Dictionary<string, string> { ["count"] = "3" });

    private static CheckDefinition Tcp(
        DateTimeOffset now,
        string name,
        int port,
        int intervalSeconds) => Check(
            now,
            CheckType.Tcp,
            $"TCP: {name}",
            intervalSeconds,
            timeoutSeconds: 5,
            new Dictionary<string, string>
            {
                ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    private static CheckDefinition Http(
        DateTimeOffset now,
        string name,
        string url,
        int intervalSeconds) => Check(
            now,
            CheckType.Http,
            $"HTTP: {name}",
            intervalSeconds,
            timeoutSeconds: 5,
            new Dictionary<string, string>
            {
                ["url"] = url,
                // Left at "any 2xx" and no content expectation: the seeded check has to keep passing
                // across a MailHog version bump, and the checklist's failure path is an operator
                // editing it rather than a fixture that rots.
                ["method"] = "GET",
            });

    private static CheckDefinition Snmp(
        DateTimeOffset now,
        string name,
        string metric,
        MonitoringSeedPlan plan,
        string community,
        int intervalSeconds,
        double? warning = null,
        double? critical = null) => Check(
            now,
            CheckType.Snmp,
            $"SNMP: {name}",
            intervalSeconds,
            timeoutSeconds: 5,
            new Dictionary<string, string>
            {
                ["metric"] = metric,
                ["version"] = "2c",
                // The simulator serves a different device profile per community string, which is how
                // one container stands in for a healthy device and a struggling one at once.
                ["community"] = community,
                ["port"] = plan.SnmpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            warning,
            critical);

    private static CheckDefinition Check(
        DateTimeOffset now,
        CheckType type,
        string name,
        int intervalSeconds,
        int timeoutSeconds,
        Dictionary<string, string> parameters,
        double? warning = null,
        double? critical = null) => new()
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Name = name,
            IntervalSeconds = intervalSeconds,
            TimeoutSeconds = timeoutSeconds,
            WarningThreshold = warning,
            CriticalThreshold = critical,
            Comparison = ThresholdComparison.GreaterThan,
            ParametersJson = JsonSerializer.Serialize(parameters),
            IsEnabled = true,
            CreatedBy = Actor,
            CreatedAt = now,
            UpdatedBy = Actor,
            UpdatedAt = now,
        };
}
