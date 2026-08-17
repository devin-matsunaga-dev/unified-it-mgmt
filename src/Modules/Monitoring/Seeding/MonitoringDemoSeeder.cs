using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;
using Modules.Monitoring.Features.PollerConfig;
using Modules.Monitoring.Features.Runbooks;

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
/// <param name="HealthyCredentialId">
/// The vault credential holding the healthy profile's community (WP-3.11), taken as an argument for the
/// same reason the CI ids are: it belongs to Platform, and a seeder does not reach across to look one
/// up. Null falls back to the plaintext <c>community</c> parameter, which is what a run against a
/// database seeded before this package looks like — and is the state every SNMP check in the estate
/// was in until now.
/// </param>
/// <param name="DegradedCredentialId">The same for the degraded profile.</param>
/// <param name="DownableSnmpAddress">
/// Host the second SNMP simulator answers on (WP-3.12). It is a container of its own so that stopping
/// it takes exactly one device away — stopping the shared simulator would take the healthy and
/// degraded devices with it, which is what made the Phase 3 demo impossible to perform on its own.
/// It serves its profile under the <c>healthy</c> community, so it needs no credential of its own.
/// </param>
/// <param name="HttpTargetAddress">
/// Host the mock HTTP target answers on (WP-3.12): a page this repository owns, so a content
/// expectation can be broken by editing a file rather than by hoping a third party's page changes.
/// </param>
/// <param name="HttpTargetPort">The port it serves on, used by both the TCP check and the URL.</param>
/// <param name="HttpTargetExpectedContent">
/// A phrase the mock target's page carries. The seeded HTTP check matches it, which is what makes
/// "break the expectation and watch the check fail" a one-line edit.
/// </param>
/// <param name="DiscoveryGroup">Which discovery service runs the seeded scan profiles (WP-4.1).</param>
/// <param name="LocalScanRange">
/// What the seeded scan profile sweeps. <c>local</c> rather than a CIDR because under
/// <c>aspire run</c> the scanner's subnet is allocated by Docker at session start — a literal range
/// would scan an address space nothing in this stack is on. The scanner resolves the keyword from its
/// own interface.
/// </param>
/// <param name="EmptyScanRange">
/// A range guaranteed to contain nothing, so the WP's second verification case — "scan a range with
/// nothing → clean empty result, no crash" — happens on every run rather than needing to be set up.
/// TEST-NET-1 again, for the same reason WP-3.3 used it for the unreachable device.
/// </param>
public sealed record MonitoringSeedPlan(
    string SnmpAddress,
    int SnmpPort,
    IReadOnlyList<Guid> CiIds,
    string PollerGroup = "default",
    string ServiceAddress = "mailhog",
    int ServiceTcpPort = 1025,
    string ServiceHttpUrl = "http://mailhog:8025/",
    Guid? HealthyCredentialId = null,
    Guid? DegradedCredentialId = null,
    string DownableSnmpAddress = "snmpsim-downable",
    string HttpTargetAddress = "http-target",
    int HttpTargetPort = 80,
    string HttpTargetExpectedContent = "Customer portal is serving normally.",
    string DiscoveryGroup = "default",
    string LocalScanRange = "local",
    string EmptyScanRange = "192.0.2.0/29");

public sealed record MonitoringSeedResult(
    int DevicesAdded,
    int ChecksAdded,
    int ScanProfilesAdded = 0,
    int RunbooksAdded = 0,
    int RunbookTriggersAdded = 0);

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

    /// <summary>
    /// The seeded mock HTTP target (WP-3.12). Named rather than repeated, because WP-5.6's seeded
    /// runbook trigger is scoped to this device and the two have to be the same device or the trigger
    /// silently matches nothing.
    /// </summary>
    public static readonly Guid HttpTargetDeviceId = Guid.Parse("0199c0de-3300-7000-8000-000000000006");

    /// <summary>The simulator profile each SNMP device reads, as a community string.</summary>
    public const string HealthyCommunity = "healthy";
    public const string DegradedCommunity = "degraded";

    public async Task<MonitoringSeedResult> SeedAsync(
        MonitoringSeedPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Guarded on its own presence rather than folded into the device check below, so that a
        // database seeded before WP-4.1 — which has devices and no scan profiles — still gets them.
        var scanProfilesAdded = await SeedScanProfilesAsync(plan, cancellationToken);
        var (runbooksAdded, triggersAdded) = await SeedRunbooksAsync(cancellationToken);

        if (await dbContext.MonitoredDevices.AnyAsync(cancellationToken))
        {
            // Idempotent by presence, like every other seeder here: a re-run against a database that
            // already has devices must add nothing rather than a second copy of each.
            return new MonitoringSeedResult(0, 0, scanProfilesAdded, runbooksAdded, triggersAdded);
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

        return new MonitoringSeedResult(
            devices.Count,
            devices.Sum(device => device.Checks.Count),
            scanProfilesAdded,
            runbooksAdded,
            triggersAdded);
    }

    /// <summary>
    /// WP-5.6's one allowlisted runbook, registered, plus a trigger narrow enough to be safe on a
    /// fresh install.
    /// <para>
    /// The trigger is scoped to the seeded mock HTTP target and to that alone. An estate-wide trigger
    /// on <c>check.success</c> would arm auto-remediation against every device the moment somebody ran
    /// <c>aspire run</c> — including the deliberately unreachable one, which fails permanently by
    /// design. Seeding the wide version would be seeding the mistake this feature is most likely to
    /// make.
    /// </para>
    /// <para>
    /// Nothing here can create a runbook the catalogue does not name: the key is
    /// <see cref="RunbookCatalog.RestartService"/> itself, so a seeded row cannot be a way past the
    /// allowlist any more than an API call can.
    /// </para>
    /// </summary>
    private async Task<(int Runbooks, int Triggers)> SeedRunbooksAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.Runbooks.AnyAsync(cancellationToken))
        {
            return (0, 0);
        }

        var definition = RunbookCatalog.Find(RunbookCatalog.RestartService);
        if (definition is null)
        {
            return (0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var runbook = new Runbook
        {
            Id = Guid.Parse("0199c0de-5600-7000-8000-000000000001"),
            Key = definition.Key,
            Name = definition.Name,
            Description = definition.Description,
            Version = 1,
            TimeoutSeconds = definition.DefaultTimeoutSeconds,
            // Deliberately tighter than the configured default. A seeded estate is one nobody is
            // watching yet, and three attempts an hour is enough to demonstrate the feature and not
            // enough to be a nuisance if a seeded check starts flapping.
            MaxExecutionsPerWindow = 3,
            RateLimitWindowMinutes = 60,
            IsEnabled = true,
            CreatedBy = Actor,
            CreatedAt = now,
            UpdatedBy = Actor,
            UpdatedAt = now,
        };
        dbContext.Runbooks.Add(runbook);

        dbContext.RunbookTriggers.Add(new RunbookTrigger
        {
            Id = Guid.Parse("0199c0de-5600-7000-8000-000000000002"),
            RunbookId = runbook.Id,
            MetricName = AlertRules.AvailabilityMetric,
            MinimumSeverity = AlertSeverity.Critical,
            // The mock HTTP target from WP-3.12, and nothing else.
            DeviceId = HttpTargetDeviceId,
            ParametersJson = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["service"] = "nginx" }),
            IsEnabled = true,
            CreatedBy = Actor,
            CreatedAt = now,
            UpdatedBy = Actor,
            UpdatedAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return (1, 1);
    }

    /// <summary>
    /// Two scan profiles, so a fresh <c>aspire run</c> gives the discovery service both of WP-4.1's
    /// verification cases without anybody creating one by hand.
    /// <para>
    /// Unlike a device, a scan profile needs no CI and writes no config-log entry: a discovery group is
    /// sent its profile list whole on every fetch, so there is no delta for a missing entry to break.
    /// That makes this a plain insert.
    /// </para>
    /// </summary>
    private async Task<int> SeedScanProfilesAsync(
        MonitoringSeedPlan plan,
        CancellationToken cancellationToken)
    {
        if (await dbContext.ScanProfiles.AnyAsync(cancellationToken))
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var profiles = new List<ScanProfile>
        {
            Profile(
                Guid.Parse("0199c0de-4100-7000-8000-000000000001"),
                "Local subnet sweep",
                "Everything on the network this scanner sits on: ping, fingerprint, then ask over SNMP.",
                plan,
                now,
                [plan.LocalScanRange],
                // The ports the seeded estate actually answers on, so the fingerprint finds something:
                // SSH, the simulators' HTTP, the API, the broker's management UI and MailHog's.
                [22, 80, 443, 5000, 8025, 15672],
                snmpEnabled: true,
                neighbourDiscoveryEnabled: true),

            Profile(
                Guid.Parse("0199c0de-4100-7000-8000-000000000002"),
                "Documentation range (finds nothing)",
                "TEST-NET-1, routed nowhere. It exists so that a scan of an empty range is something "
                    + "an operator can watch complete cleanly rather than take on trust.",
                plan,
                now,
                [plan.EmptyScanRange],
                // No ports and no SNMP: nothing answers, and the point of this profile is that the
                // sweep alone finishes and reports zero.
                [],
                snmpEnabled: false,
                neighbourDiscoveryEnabled: false),
        };

        dbContext.ScanProfiles.AddRange(profiles);
        await dbContext.SaveChangesAsync(cancellationToken);
        return profiles.Count;
    }

    private static ScanProfile Profile(
        Guid id,
        string name,
        string description,
        MonitoringSeedPlan plan,
        DateTimeOffset now,
        IReadOnlyList<string> ranges,
        IReadOnlyList<int> ports,
        bool snmpEnabled,
        bool neighbourDiscoveryEnabled) => new()
        {
            Id = id,
            Name = name,
            Description = description,
            DiscoveryGroup = plan.DiscoveryGroup,
            RangesJson = JsonSerializer.Serialize(ranges),
            PortsJson = JsonSerializer.Serialize(ports),
            // Frequent for a scan, because a fresh run should discover the estate while somebody is
            // still watching it. A real estate scans hourly or nightly.
            IntervalMinutes = 5,
            TimeoutSeconds = 2,
            SnmpEnabled = snmpEnabled,
            NeighbourDiscoveryEnabled = neighbourDiscoveryEnabled,
            IsEnabled = true,
            CreatedBy = Actor,
            CreatedAt = now,
            UpdatedBy = Actor,
            UpdatedAt = now,
        };

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
                Snmp(now, "System information", "sysinfo", plan, HealthyCommunity,
                    plan.HealthyCredentialId, 300),
                Snmp(now, "CPU", "cpu", plan, HealthyCommunity, plan.HealthyCredentialId, 60,
                    warning: 70, critical: 90),
                Snmp(now, "Memory", "memory", plan, HealthyCommunity, plan.HealthyCredentialId, 60,
                    warning: 80, critical: 95),
                // WP-4.5. Thirty seconds rather than sixty, for the same reason the down-able device
                // polls at thirty: the WP's verification is to take a port down by hand and wait for
                // the alert, and three sustained cycles at a minute is a three-minute silence with
                // nothing on screen to say anything is happening.
                //
                // The thresholds are percent of link speed, judged per port — an interface check has
                // no check-wide rule of its own. Nothing on the simulated switch runs near them
                // (the busiest port is 10% of a gigabit), so the seeded estate stays quiet and the
                // utilisation alert is demonstrated by lowering the threshold rather than by a port
                // that is permanently over it.
                Snmp(now, "Interfaces", "interfaces", plan, HealthyCommunity, plan.HealthyCredentialId, 30,
                    warning: 70, critical: 90),
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
                Snmp(now, "CPU", "cpu", plan, DegradedCommunity, plan.DegradedCredentialId, 60,
                    warning: 70, critical: 90),
                Snmp(now, "Memory", "memory", plan, DegradedCommunity, plan.DegradedCredentialId, 60,
                    warning: 80, critical: 95),
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

        // WP-3.12's down-able device, and the only one in the estate that can be taken away without
        // taking anything else with it: it is the sole occupant of its own simulator container, so
        // `docker stop snmpsim-downable` fails its checks and leaves every other device polling. That
        // is the Phase 3 demo — one device down, one ticket, revive, the ticket resolves itself.
        //
        // Its checks are the ones that make the loop run quickly enough to watch: a 30-second interval
        // against the platform's default of three sustained cycles is an alert about a minute and a
        // half after the container stops. It reads its community from the same vaulted credential as
        // the healthy device (see MonitoringSeedPlan.DownableSnmpAddress).
        if (plan.CiIds.Count < 5)
        {
            yield break;
        }

        yield return Device(
            Guid.Parse("0199c0de-3300-7000-8000-000000000005"),
            plan.CiIds[4],
            plan,
            now,
            "Simulated switch — down-able profile (stop `snmpsim-downable` to take it away)",
            plan.DownableSnmpAddress,
            [
                Ping(now, "Reachability", intervalSeconds: 30),
                Snmp(now, "CPU", "cpu", plan, HealthyCommunity, plan.HealthyCredentialId, 30,
                    warning: 70, critical: 90),
                Snmp(now, "Memory", "memory", plan, HealthyCommunity, plan.HealthyCredentialId, 30,
                    warning: 80, critical: 95),
            ]);

        // WP-3.12's mock HTTP target. Unlike the mail device above, this one's page belongs to this
        // repository, which is what lets the seeded check carry a content expectation at all: editing
        // `src/AppHost/http-target/index.html` breaks it with no restart and nothing else in the stack
        // notices. The mail device keeps its expectation-free check, because MailHog's UI is MailHog's
        // to change.
        if (plan.CiIds.Count < 6)
        {
            yield break;
        }

        yield return Device(
            HttpTargetDeviceId,
            plan.CiIds[5],
            plan,
            now,
            "Customer portal — mock HTTP target, with a content expectation to break",
            plan.HttpTargetAddress,
            [
                Tcp(now, "Portal port", plan.HttpTargetPort, intervalSeconds: 30),
                Http(
                    now,
                    "Portal page",
                    $"http://{plan.HttpTargetAddress}:{plan.HttpTargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}/",
                    intervalSeconds: 30,
                    expectedContent: plan.HttpTargetExpectedContent),
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
        int intervalSeconds,
        string? expectedContent = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["url"] = url,
            // Left at "any 2xx": a status expectation is one status code (WP-3.8), and pinning it
            // would make a redirect somebody adds to the page read as an outage.
            ["method"] = "GET",
        };

        // Only where the page is this repository's. Against a third party's — MailHog's UI is the
        // seeded example — a content expectation is a fixture that rots on their next version bump,
        // which is why the mail device's check deliberately still carries none.
        if (expectedContent is not null)
        {
            parameters["expectedContent"] = expectedContent;
        }

        return Check(now, CheckType.Http, $"HTTP: {name}", intervalSeconds, timeoutSeconds: 5, parameters);
    }

    private static CheckDefinition Snmp(
        DateTimeOffset now,
        string name,
        string metric,
        MonitoringSeedPlan plan,
        string community,
        Guid? credentialId,
        int intervalSeconds,
        double? warning = null,
        double? critical = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["metric"] = metric,
            ["version"] = "2c",
            ["port"] = plan.SnmpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        // The simulator serves a different device profile per community string, which is how one
        // container stands in for a healthy device and a struggling one at once — so the community is
        // what makes these two devices different, and it is exactly the kind of thing WP-3.11 exists
        // to stop storing in the clear. With a credential the parameter is omitted entirely rather
        // than left beside it: a stale copy in `parameters` would keep polling after a rotation and
        // make the vault look like it was working when it was being bypassed.
        if (credentialId is null)
        {
            parameters["community"] = community;
        }

        return Check(
            now,
            CheckType.Snmp,
            $"SNMP: {name}",
            intervalSeconds,
            timeoutSeconds: 5,
            parameters,
            warning,
            critical,
            credentialId);
    }

    private static CheckDefinition Check(
        DateTimeOffset now,
        CheckType type,
        string name,
        int intervalSeconds,
        int timeoutSeconds,
        Dictionary<string, string> parameters,
        double? warning = null,
        double? critical = null,
        Guid? credentialId = null) => new()
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
            CredentialId = credentialId,
            IsEnabled = true,
            CreatedBy = Actor,
            CreatedAt = now,
            UpdatedBy = Actor,
            UpdatedAt = now,
        };
}
