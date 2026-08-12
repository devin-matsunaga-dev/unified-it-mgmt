using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

using Contracts.Events;

using MassTransit;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.AlertTickets;
using Modules.Monitoring.Data;

using Platform.Data;

using StackExchange.Redis;

namespace Infrastructure.Tests;

/// <summary>
/// The whole loop in one test: a simulated device degrades, an alert is raised, a ticket opens,
/// the device recovers, the alert clears and the ticket resolves itself.
/// <para>
/// Every other test in this project proves one link of that chain against the real infrastructure.
/// None of them proves the chain. This one drives poller-shaped telemetry onto a real broker and
/// then reads the ticket at the far end, through the real host — the real alert engine, the real
/// transactional outbox, the real Helpdesk consumers, a real Postgres and a real Redis. The only
/// thing standing in for reality is the poller process itself: the readings are published as
/// <see cref="DeviceTelemetryReported"/> rather than measured over SNMP, exactly as
/// <see cref="PollerTelemetryBusIntegrationTests"/> does. What the Python poller puts on the wire is
/// a separate contract, guarded by <see cref="PollerEnvelopeTests"/> against a fixture the poller's
/// own suite asserts it emits; re-proving it here would make this test depend on it twice.
/// </para>
/// <para>
/// The thresholds are the guard. <see cref="Pipeline_ReadingsBelowTheThreshold_RaiseNothing"/> drives
/// a quiet check through the same batches as a breaching one, so a configuration change that makes
/// everything alert fails here rather than passing quietly — which is what the WP means by the E2E
/// being a real guard.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class MonitoringPipelineE2ETests : IAsyncLifetime
{
    private readonly PipelineApplication _application;
    private readonly string _redisConnectionString;
    private HttpClient? _client;

    public MonitoringPipelineE2ETests(InfrastructureFixture infrastructure)
    {
        _redisConnectionString = infrastructure.RedisConnectionString;
        _application = new PipelineApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.RedisConnectionString,
            infrastructure.MinioConnectionString);
        Infrastructure = infrastructure;
    }

    private InfrastructureFixture Infrastructure { get; }

    public async Task InitializeAsync()
    {
        // The host migrates all four schemas itself (`Platform:ApplyMigrations`, left at its default)
        // before the bus starts, which is the production path and the only ordering under which a
        // consumer cannot reach a table that does not exist yet.
        _client = _application.CreateClient();

        // The breaker counts every ticket the whole collection has opened through the shared Redis,
        // and a run that arrives with it already tripped would see no ticket at all. The threshold is
        // raised in configuration as well; this clears whatever a previous run left behind.
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString);
        await connection.GetDatabase().KeyDeleteAsync(
            [RedisAlertAutomationGuard.BreakerKey, RedisAlertAutomationGuard.WindowKey]);
    }

    /// <summary>
    /// Shutting this host down is not housekeeping — it is correctness for the rest of the suite.
    /// <para>
    /// Unlike every other host in this project, this one runs the real bus <em>and</em> a deliberately
    /// fast outbox sweep. The transactional outbox is a shared Platform table, so a sweeper left
    /// running keeps delivering and removing rows written by whichever test class runs next — and
    /// <see cref="AlertEngineIntegrationTests"/> reads that table as its evidence that the engine
    /// published. Leaving this host alive failed
    /// <c>Evaluate_SustainedBreach_RaisesOneCriticalAlertAndPublishesItOnce</c> with an empty
    /// collection, minutes after this class had finished and from a completely different package's
    /// code. Dispose stops the bus and the sweeper with it.
    /// </para>
    /// </summary>
    public async Task DisposeAsync() => await _application.DisposeAsync();

    /// <summary>
    /// The WP's E2E: sim degrades → alert → ticket → sim recovers → ticket resolved.
    /// <para>
    /// One test for the whole loop on purpose. The recovery half can only be proved against a ticket
    /// the degradation half opened, and splitting them would mean driving the first half twice.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pipeline_SimDegradesThenRecovers_OpensATicketAndResolvesIt()
    {
        var device = await CreateDeviceWithCpuCheckAsync();
        var ruleId = $"check:{device.CheckId}:cpu.utilisation_percent";
        var dedupeKey = AlertTicketPolicy.DedupeKey(device.DeviceId, ruleId);

        // --- the sim degrades ---
        // Three cycles, because the platform default is not to believe a breach until it has been
        // seen three times running. Two would prove nothing about the pipeline and everything about
        // the sustain count.
        await DriveAsync(device, ruleId, [95, 96, 97]);

        var alert = await WaitForAlertAsync(device.DeviceId, ruleId, item => item.Status == AlertStatus.Open);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(90d, alert.Threshold);
        Assert.Equal(97d, alert.LastValue);

        // --- which becomes a ticket ---
        var opened = await WaitForAlertTicketAsync(dedupeKey, entry => entry?.TicketId is not null);
        var ticket = await TicketAsync(opened.TicketId!.Value);
        Assert.Equal(1, opened.OccurrenceCount);
        Assert.Equal(0, opened.SuppressedCount);
        // Not yet resolved: the point of the second half is that something changes it.
        Assert.NotEqual("Resolved", ticket.Status.Name);
        // The alert's own words reached the ticket, so this is that alert's ticket rather than one
        // that happened to be open.
        Assert.Contains("CPU", ticket.Title, StringComparison.OrdinalIgnoreCase);

        // The other consumer of the same telemetry. Proving the readings landed as well as alerted is
        // most of the difference between "the alert engine works" and "the pipeline works".
        Assert.NotEmpty(await MetricsAsync(device.DeviceId, "cpu.utilisation_percent"));

        // --- the sim recovers ---
        // Two good readings is the platform's recovery count; a third gives the clear somewhere to
        // land if the first is the one that only starts the run.
        await DriveAsync(device, ruleId, [9, 8, 7]);

        var cleared = await WaitForAlertAsync(
            device.DeviceId, ruleId, item => item.Status == AlertStatus.Cleared);
        Assert.Equal(alert.Id, cleared.Id);
        Assert.NotNull(cleared.ClearedAt);

        // --- and the ticket resolves itself ---
        var resolved = await WaitForAlertTicketAsync(dedupeKey, entry => entry?.AutoResolvedAt is not null);
        var resolvedTicket = await TicketAsync(resolved.TicketId!.Value);
        Assert.Equal("Resolved", resolvedTicket.Status.Name);
        // The same ticket, walked to Resolved — not a second one opened by the clear.
        Assert.Equal(opened.TicketId, resolved.TicketId);
        Assert.Equal(1, resolved.TicketCount);
    }

    /// <summary>
    /// The failure path, and the reason the E2E is a guard rather than a demonstration: a check whose
    /// readings never cross its threshold must produce no alert and no ticket.
    /// <para>
    /// Both checks report in the <em>same</em> telemetry batch, which is what makes the negative
    /// deterministic: by the time the breaching rule has raised, the quiet rule has been evaluated
    /// against exactly the same messages. Waiting a fixed period and asserting nothing appeared would
    /// pass on a slow machine for the wrong reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pipeline_ReadingsBelowTheThreshold_RaiseNothing()
    {
        var device = await CreateDeviceWithCpuCheckAsync();
        var quietCheckId = await AddCpuCheckAsync(device.DeviceId, "SNMP: CPU (quiet)");
        var breachingRuleId = $"check:{device.CheckId}:cpu.utilisation_percent";
        var quietRuleId = $"check:{quietCheckId}:cpu.utilisation_percent";

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var observedAt = Observed(cycle);
            await PublishAsync(new DeviceTelemetryReported(
                Guid.CreateVersion7(), observedAt, "poller-e2e", device.PollerGroup, cycle,
                [
                    Reading(device, device.CheckId, observedAt, 95d),
                    // Under the 70 warning threshold, so this rule stays Ok however long it runs.
                    Reading(device, quietCheckId, observedAt, 40d),
                ]));
            await WaitForEvaluationAsync(device.DeviceId, breachingRuleId, observedAt);
        }

        await WaitForAlertAsync(device.DeviceId, breachingRuleId, item => item.Status == AlertStatus.Open);
        await WaitForAlertTicketAsync(
            AlertTicketPolicy.DedupeKey(device.DeviceId, breachingRuleId),
            entry => entry?.TicketId is not null);

        Assert.Empty(await AlertsAsync(device.DeviceId, quietRuleId));
        Assert.Null(await AlertTicketAsync(AlertTicketPolicy.DedupeKey(device.DeviceId, quietRuleId)));
    }

    // ---- driving ----

    private sealed record DeviceFixture(Guid DeviceId, Guid CiId, Guid CheckId, string Address, string PollerGroup);

    /// <summary>
    /// An hour back, so the readings never sit in the future however long the run takes, and a minute
    /// apart, which is the check's own interval.
    /// </summary>
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-1);

    private int _cycle;

    private DateTimeOffset Observed(int cycle) => Base.AddMinutes(cycle);

    /// <summary>
    /// One cycle per reading, each published and then waited for.
    /// <para>
    /// The waiting is load-bearing. The alert engine's "for N cycles" counter is a read-modify-write
    /// against Redis, and MassTransit will happily hand a consumer several messages from one queue at
    /// once — three cycles published back to back can interleave, both read a counter of one, and the
    /// run to three never completes. Waiting for the state to record each reading before sending the
    /// next is what a poller on a fifteen-second interval does anyway.
    /// </para>
    /// </summary>
    private async Task DriveAsync(DeviceFixture device, string ruleId, IReadOnlyList<double> readings)
    {
        foreach (var reading in readings)
        {
            var observedAt = Observed(_cycle++);
            await PublishAsync(new DeviceTelemetryReported(
                Guid.CreateVersion7(), observedAt, "poller-e2e", device.PollerGroup, _cycle,
                [Reading(device, device.CheckId, observedAt, reading)]));
            await WaitForEvaluationAsync(device.DeviceId, ruleId, observedAt);
        }
    }

    private static DeviceCheckResult Reading(
        DeviceFixture device,
        Guid checkId,
        DateTimeOffset observedAt,
        double cpu) => new(
        device.DeviceId, device.CiId, checkId, "Snmp", "SNMP: CPU", device.Address, observedAt,
        Succeeded: true, LatencyMs: 4, Error: null,
        Metrics: [new MetricSample("cpu.utilisation_percent", cpu, null, "%")]);

    private async Task PublishAsync(DeviceTelemetryReported telemetry)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        // Through the outbox, like every publish in this solution (ARCHITECTURE §4).
        await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(telemetry);
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
    }

    // ---- waiting ----

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Waits until the rule's Redis state records this reading, which is the one signal that the alert
    /// engine — rather than the metrics consumer beside it — has seen the message.
    /// </summary>
    private async Task WaitForEvaluationAsync(Guid deviceId, string ruleId, DateTimeOffset observedAt)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString);
        var key = $"monitoring:alert-state:{deviceId}:{ruleId}";
        var deadline = DateTimeOffset.UtcNow + Timeout;
        string? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await connection.GetDatabase().StringGetAsync(key);
            if (!value.IsNullOrEmpty)
            {
                last = value!;
                using var state = JsonDocument.Parse(last);
                if (state.RootElement.TryGetProperty("lastObservedAt", out var seen)
                    && seen.ValueKind is JsonValueKind.String
                    && seen.GetDateTimeOffset() >= observedAt)
                {
                    return;
                }
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"The alert engine never evaluated the reading observed at {observedAt:O} for '{ruleId}'. "
            + $"State: {last ?? "(the key does not exist — nothing has evaluated this rule at all)"}. "
            + await DiagnoseAsync());
    }

    private async Task<Alert> WaitForAlertAsync(Guid deviceId, string ruleId, Func<Alert, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var alerts = await AlertsAsync(deviceId, ruleId);
            if (alerts.FirstOrDefault(condition) is { } match)
            {
                return match;
            }

            await Task.Delay(200);
        }

        var found = await AlertsAsync(deviceId, ruleId);
        throw new TimeoutException(
            $"No alert on '{ruleId}' satisfied the condition (found {found.Count}: "
            + $"{string.Join(", ", found.Select(alert => $"{alert.Severity}/{alert.Status}"))}). "
            + await DiagnoseAsync());
    }

    private async Task<AlertTicket> WaitForAlertTicketAsync(string dedupeKey, Func<AlertTicket?, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        AlertTicket? entry = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            entry = await AlertTicketAsync(dedupeKey);
            if (condition(entry))
            {
                return entry!;
            }

            await Task.Delay(200);
        }

        // "It never arrived", "it arrived and was declined" and "it arrived and faulted" all look the
        // same from a bare timeout, and this is what tells them apart — the same diagnostic
        // AlertTicketBusIntegrationTests carries, for the same three failures.
        var state = entry is null
            ? "no alert-ticket row was written at all"
            : $"row: tickets={entry.TicketCount}, suppressed={entry.SuppressedCount}, "
              + $"occurrences={entry.OccurrenceCount}, ticketId={entry.TicketId}, resolved={entry.AutoResolvedAt}";
        throw new TimeoutException($"Nothing satisfied the condition for '{dedupeKey}' ({state}). "
            + await DiagnoseAsync());
    }

    private async Task<string> DiagnoseAsync()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var undelivered = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database
            .SqlQuery<int>(
                $"select count(*)::int as \"Value\" from platform.outbox_states where delivered is null")
            .SingleAsync();
        return $"Undelivered outbox states: {undelivered}. Queues:{Environment.NewLine}"
            + await Infrastructure.DescribeRabbitMqQueuesAsync();
    }

    // ---- reading back ----

    private async Task<IReadOnlyList<Alert>> AlertsAsync(Guid deviceId, string ruleId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Alerts
            .AsNoTracking()
            .Where(alert => alert.DeviceId == deviceId && alert.RuleId == ruleId)
            .OrderBy(alert => alert.RaisedAt)
            .ToListAsync();
    }

    private async Task<AlertTicket?> AlertTicketAsync(string dedupeKey)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().AlertTickets
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.DedupeKey == dedupeKey);
    }

    private async Task<Modules.Helpdesk.Data.Ticket> TicketAsync(Guid ticketId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Tickets
            .AsNoTracking()
            .Include(ticket => ticket.Status)
            .SingleAsync(ticket => ticket.Id == ticketId);
    }

    private async Task<IReadOnlyList<DeviceMetric>> MetricsAsync(Guid deviceId, string metricName)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().DeviceMetrics
            .AsNoTracking()
            .Where(metric => metric.DeviceId == deviceId && metric.MetricName == metricName)
            .ToListAsync();
    }

    // ---- fixtures ----

    private async Task<DeviceFixture> CreateDeviceWithCpuCheckAsync()
    {
        var ci = await CreateCiAsync();
        // A group of its own, so nothing this test writes appears in another test's poller
        // configuration and nothing another test writes appears in its telemetry.
        var pollerGroup = $"e2e-{Guid.NewGuid():N}"[..20];

        using var request = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        request.Content = JsonContent.Create(new { ciId = ci.Id, address = "10.60.0.1", pollerGroup });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var device = Assert.IsType<DeviceDto>(await response.Content.ReadFromJsonAsync<DeviceDto>());

        var checkId = await AddCpuCheckAsync(device.Id, "SNMP: CPU");
        return new DeviceFixture(device.Id, device.CiId, checkId, device.Address, pollerGroup);
    }

    /// <summary>
    /// Warning at 70, critical at 90 — the same pair the seeded estate carries, because these are the
    /// numbers the WP's "break the threshold config and watch the E2E fail" step edits.
    /// </summary>
    private async Task<Guid> AddCpuCheckAsync(Guid deviceId, string name)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/monitored-devices/{deviceId}/checks");
        request.Content = JsonContent.Create(new
        {
            type = "Snmp",
            name,
            intervalSeconds = 60,
            timeoutSeconds = 5,
            warningThreshold = 70d,
            criticalThreshold = 90d,
            comparison = "GreaterThan",
            parameters = new Dictionary<string, string>
            {
                // WP-3.1's `RequiredParameter[Snmp] = "oid"` defect: a `metric` check with no `oid` is
                // refused by its own API although the poller runs it happily. Narrow this when that is
                // fixed, as `Seed_EveryCheck_PassesTheRulesItsOwnApiWouldApply` already notes.
                ["oid"] = "1.3.6.1.2.1.25.3.3.1.2",
                ["metric"] = "cpu",
            },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var check = await response.Content.ReadFromJsonAsync<CheckDto>();
        return check!.Id;
    }

    private async Task<CiDto> CreateCiAsync()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "NetworkDevice",
            name = $"Sim switch {Guid.NewGuid():N}",
            attributes = new Dictionary<string, string>
            {
                ["managementIp"] = "10.60.0.1",
                ["vendor"] = "Simulated",
                ["portCount"] = "24",
            },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(PipelineAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record DeviceDto(Guid Id, Guid CiId, string Address, string PollerGroup);

    private sealed record CheckDto(Guid Id, string Name);

    /// <summary>
    /// The real host with the real bus. Unlike <see cref="AlertEngineIntegrationTests"/>, which turns
    /// the bus off so its publications stay in the outbox where it can read them, this host has to
    /// deliver them: the ticket at the far end exists only because <c>AlertRaised</c> travelled the
    /// broker into a consumer in another module.
    /// </summary>
    private sealed class PipelineApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _redisConnectionString;
        private readonly string _minioConnectionString;

        public PipelineApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string redisConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _redisConnectionString = redisConnectionString;
            _minioConnectionString = minioConnectionString;
            // Aspire's AddNpgsqlDataSource and AddRedisClient read the builder's configuration while
            // the host is being built, before WebApplicationFactory's own sources exist — the same
            // reason AlertEngineIntegrationTests sets these as environment variables too.
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__redis", redisConnectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", rabbitMqConnectionString);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = "https://identity.example.test/realms/it-platform",
                    ["Authentication:Audience"] = "it-platform-api",
                    ["Authentication:ClientId"] = "it-platform-web",
                    ["Authentication:PostLogoutRedirectUri"] = "https://app.example.test/",
                    ["ConnectionStrings:database"] = _connectionString,
                    ["ConnectionStrings:rabbitmq"] = _rabbitMqConnectionString,
                    ["ConnectionStrings:redis"] = _redisConnectionString,
                    ["ConnectionStrings:minio"] = _minioConnectionString,
                    ["ObjectStorage:AccessKey"] = "minioadmin",
                    ["ObjectStorage:SecretKey"] = "minio-test-password",
                    // Quartz off: nothing in this loop is scheduled, and its jobs would run against
                    // every other test's rows in the shared database.
                    ["Platform:EnableScheduler"] = "false",
                    // The circuit breaker is global by design (WP-3.6) and its Redis counters are
                    // shared with every other test in the collection. Left at the default, a suite
                    // that has already opened ten automated tickets would suppress this one and the
                    // failure would read as a broken pipeline.
                    [$"{AlertTicketOptions.SectionName}:BreakerThreshold"] = "1000",
                }));
            builder.ConfigureServices(services => services
                // Registered here, which is after AddPlatformServices: options configure actions run
                // in registration order, so anything set before it is overwritten by MassTransit's
                // own defaults (the trap AlertTicketBusIntegrationTests records).
                .Configure<MassTransitHostOptions>(options =>
                {
                    // So the first publish cannot race the bus into declaring its queues and
                    // bindings: a message published to a fanout with nothing bound is discarded,
                    // leaving an empty queue, no error queue and nothing to explain it.
                    options.WaitUntilStarted = true;
                    options.StartTimeout = TimeSpan.FromSeconds(60);
                })
                // The outbox is a shared Platform table and every test host that runs on the
                // in-memory bus leaves its rows undelivered, so by the time this class runs there is
                // a backlog ahead of ours that the default sweep is far too slow to reach.
                .Configure<OutboxDeliveryServiceOptions>(options =>
                {
                    options.QueryDelay = TimeSpan.FromMilliseconds(250);
                    options.QueryMessageLimit = 500;
                })
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = PipelineAuthenticationHandler.TestScheme;
                    options.DefaultChallengeScheme = PipelineAuthenticationHandler.TestScheme;
                    options.DefaultForbidScheme = PipelineAuthenticationHandler.TestScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, PipelineAuthenticationHandler>(
                    PipelineAuthenticationHandler.TestScheme,
                    _ => { }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__redis", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", null);
        }
    }

    private sealed class PipelineAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "PipelineTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].ToString();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "pipeline-test-user-id"),
                    new Claim(ClaimTypes.Name, "pipeline-test-user"),
                    new Claim(ClaimTypes.Role, role),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
