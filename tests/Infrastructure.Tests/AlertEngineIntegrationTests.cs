using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

using Contracts.Events;

using MassTransit.EntityFrameworkCoreIntegration;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Assets.Data;
using Modules.Helpdesk.Data;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;

using Platform.Data;

using StackExchange.Redis;

namespace Infrastructure.Tests;

/// <summary>
/// The WP's verification list against the real thing: fake telemetry driven cycle by cycle through
/// the engine, with a real Postgres holding the alert rows, a real Redis holding the state machines
/// and the real transactional outbox holding what was published.
/// <para>
/// The state machine's own rules are proved without infrastructure in
/// <c>AlertStateMachineTests</c>. What is proved here is everything that class cannot see: that the
/// durable row and the Redis state agree, that the events reach the outbox, that a maintenance
/// window read out of the database silences a real device, and that flushing Redis does not re-raise
/// an alert that is already open.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class AlertEngineIntegrationTests : IAsyncLifetime
{
    private readonly AlertApplication _application;
    private readonly string _redisConnectionString;
    private HttpClient? _client;

    public AlertEngineIntegrationTests(InfrastructureFixture infrastructure)
    {
        _redisConnectionString = infrastructure.RedisConnectionString;
        _application = new AlertApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.RedisConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
        // WP-3.7's enrichment asks Helpdesk what is already open for the CI, so the helpdesk schema
        // has to exist here too — the engine reads it on every publication.
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MonitoringMigrations_CurrentModel_HasNoPendingChanges()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // ---- the N-cycle rule, end to end ----

    /// <summary>
    /// The WP's first verification step. Two cycles past the threshold is not three, so nothing is
    /// raised, nothing is stored and nothing is published — the counter is the only thing that moved.
    /// </summary>
    [Fact]
    public async Task Evaluate_ThresholdCrossedTwiceOfThree_RaisesNothing()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();

        await DriveAsync(fixture, [95, 95]);

        Assert.Empty(await AlertsAsync(fixture.DeviceId));
        // This rule's messages, not the whole outbox. The Platform tables are shared by the entire
        // collection, so a global count here would pass or fail on test order — the WP-3.4 trap.
        Assert.Empty(await PublishedAsync<AlertRaised>(
            fixture.DeviceId, $"check:{fixture.CheckId}:cpu.utilisation_percent"));
    }

    /// <summary>
    /// The WP's second: sustained → Critical raised exactly once, however many cycles keep saying so.
    /// </summary>
    [Fact]
    public async Task Evaluate_SustainedBreach_RaisesOneCriticalAlertAndPublishesItOnce()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();

        await DriveAsync(fixture, [95, 95, 95, 96, 97, 98]);

        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(AlertStatus.Open, alert.Status);
        Assert.Equal(AlertSuppression.None, alert.Suppression);
        Assert.Equal(90d, alert.Threshold);
        Assert.Equal(98d, alert.LastValue);
        Assert.Equal("cpu.utilisation_percent", alert.MetricName);
        Assert.Equal($"check:{fixture.CheckId}:cpu.utilisation_percent", alert.RuleId);

        var raised = await PublishedAsync<AlertRaised>(fixture.DeviceId, alert.RuleId);
        Assert.Single(raised);
        Assert.Equal("Critical", raised[0].Severity);
        Assert.Equal(alert.Id, raised[0].AlertId);
        Assert.Equal(fixture.CiId, raised[0].CiId);
        Assert.Equal(90d, raised[0].Threshold);
    }

    /// <summary>
    /// The WP's third: recovery → a single Cleared. The row closes and keeps its history rather than
    /// disappearing, because WP-3.6 will want to auto-resolve the ticket this alert opened.
    /// </summary>
    [Fact]
    public async Task Evaluate_RecoveryAfterAnAlert_ClearsOnceAndClosesTheRow()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();

        await DriveAsync(fixture, [95, 95, 95, 10, 10, 10, 10]);

        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertStatus.Cleared, alert.Status);
        Assert.NotNull(alert.ClearedAt);

        Assert.Single(await PublishedAsync<AlertRaised>(fixture.DeviceId, alert.RuleId));
        var cleared = Assert.Single(await PublishedAsync<AlertCleared>(fixture.DeviceId, alert.RuleId));
        Assert.Equal(alert.Id, cleared.AlertId);
        Assert.Equal("Critical", cleared.PreviousSeverity);
        Assert.True(cleared.DurationSeconds > 0);
    }

    /// <summary>
    /// A rule that recurs opens a new alert rather than reviving the closed one, which is what keeps
    /// "how many times has this happened" answerable and lets the filtered unique index be unique.
    /// </summary>
    [Fact]
    public async Task Evaluate_AProblemThatRecurs_OpensASecondAlert()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();

        await DriveAsync(fixture, [95, 95, 95, 10, 10, 95, 95, 95]);

        var alerts = await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true);
        Assert.Equal(2, alerts.Count);
        Assert.Single(alerts, alert => alert.Status == AlertStatus.Cleared);
        Assert.Single(alerts, alert => alert.Status == AlertStatus.Open);
        Assert.Equal(2, alerts.Select(alert => alert.Id).Distinct().Count());
    }

    // ---- availability ----

    /// <summary>
    /// A failing check raises on its availability rule, which needs no thresholds configured. This is
    /// the rule that makes a device that stopped answering an alert rather than a gap in a chart.
    /// </summary>
    [Fact]
    public async Task Evaluate_ACheckThatKeepsFailing_RaisesOnTheAvailabilityRule()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();

        await DriveFailuresAsync(fixture, cycles: 3);

        var alert = Assert.Single(
            await AlertsAsync(fixture.DeviceId), alert => alert.MetricName == "check.success");
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal($"check:{fixture.CheckId}:availability", alert.RuleId);
        Assert.Contains("Timed out", alert.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A device that goes away must not also clear the threshold alert it already had: nobody
    /// measured its CPU, and "unmeasured" is not "fine".
    /// </summary>
    [Fact]
    public async Task Evaluate_WhenADeviceStopsAnswering_TheThresholdAlertStaysOpen()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        await DriveAsync(fixture, [95, 95, 95]);

        await DriveFailuresAsync(fixture, cycles: 4);

        var threshold = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertStatus.Open, threshold.Status);
        Assert.Equal(AlertSeverity.Critical, threshold.Severity);
    }

    // ---- maintenance windows ----

    /// <summary>
    /// The WP's fifth verification step, with a real window read out of the database: an active window
    /// over this device silences the alert without blinding the engine.
    /// </summary>
    [Fact]
    public async Task Evaluate_InsideAnActiveMaintenanceWindow_RecordsTheAlertAndPublishesNothing()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        await CreateMaintenanceWindowAsync(fixture.DeviceId);

        await DriveAsync(fixture, [95, 95, 95, 95]);

        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertStatus.Open, alert.Status);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(AlertSuppression.Maintenance, alert.Suppression);

        Assert.Empty(await PublishedAsync<AlertRaised>(fixture.DeviceId, alert.RuleId));
    }

    /// <summary>
    /// A window that has already ended mutes nothing. Worth its own test: the window query is the one
    /// place a wrong comparison silences an entire estate and every other test would still pass.
    /// </summary>
    [Fact]
    public async Task Evaluate_WithAnExpiredMaintenanceWindow_IsNotMuted()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        await CreateMaintenanceWindowAsync(
            fixture.DeviceId,
            startsAt: DateTimeOffset.UtcNow.AddHours(-3),
            endsAt: DateTimeOffset.UtcNow.AddHours(-2));

        await DriveAsync(fixture, [95, 95, 95]);

        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertSuppression.None, alert.Suppression);
        Assert.Single(await PublishedAsync<AlertRaised>(fixture.DeviceId, alert.RuleId));
    }

    // ---- Redis is not the source of truth ----

    /// <summary>
    /// ARCHITECTURE §5 says Redis must never hold anything that has to survive a flush. Flushing it
    /// mid-alert costs the counters and the flap history; it must not re-raise an alert that is
    /// already open, because WP-3.6 would open a second ticket for the same problem.
    /// </summary>
    [Fact]
    public async Task Evaluate_AfterRedisIsFlushed_DoesNotRaiseTheSameAlertAgain()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        await DriveAsync(fixture, [95, 95, 95]);

        var ruleId = $"check:{fixture.CheckId}:cpu.utilisation_percent";
        Assert.Single(await PublishedAsync<AlertRaised>(fixture.DeviceId, ruleId));

        await FlushAlertStateAsync(fixture.DeviceId, ruleId);
        await DriveAsync(fixture, [96, 97, 98, 99]);

        // Still one alert, still one message: the open row told the rebuilt state it was already
        // raised and already published.
        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertStatus.Open, alert.Status);
        Assert.Single(await PublishedAsync<AlertRaised>(fixture.DeviceId, ruleId));
    }

    /// <summary>
    /// The state a rule carries between cycles genuinely lives in Redis, and it is JSON somebody can
    /// read. If this key stops appearing, the engine has quietly become stateless and the N-cycle
    /// rule is counting inside one batch only.
    /// </summary>
    [Fact]
    public async Task Evaluate_StoresTheRulesStateInRedis()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        await DriveAsync(fixture, [95, 95]);

        var ruleId = $"check:{fixture.CheckId}:cpu.utilisation_percent";
        var stored = await ReadAlertStateAsync(fixture.DeviceId, ruleId);

        Assert.False(stored.IsNullOrEmpty);
        Assert.Contains("candidateCount", stored.ToString(), StringComparison.Ordinal);
    }

    // ---- per-check tuning ----

    /// <summary>
    /// The columns this WP added, doing something. A check tuned to alert on the first breach does,
    /// which is the difference between configuration and decoration.
    /// </summary>
    [Fact]
    public async Task Evaluate_ForACheckTunedToOneCycle_RaisesOnTheFirstBreach()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync(sustainedCycles: 1);

        await DriveAsync(fixture, [95]);

        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    /// <summary>The tuning is served back, so an operator can read what a check is actually running on.</summary>
    [Fact]
    public async Task CreateCheck_WithAlertTuning_RoundTripsIt()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync(sustainedCycles: 5);

        var checks = await GetAsync<List<CheckDto>>($"/api/monitored-devices/{fixture.DeviceId}/checks");
        var check = Assert.Single(checks, candidate => candidate.Id == fixture.CheckId);

        Assert.Equal(5, check.AlertTuning?.SustainedCycles);
        Assert.Null(check.AlertTuning?.RecoveryCycles);
    }

    // ---- failure paths ----

    /// <summary>
    /// A sustain count of zero would alert on the first dropped packet and a hysteresis of 100 would
    /// make an alert permanent. Both are refused at the edge rather than stored and puzzled over.
    /// </summary>
    [Fact]
    public async Task CreateCheck_WithAnImpossibleSustainCount_ReturnsValidationProblem()
    {
        var device = await CreateDeviceAsync();

        using var request = Authenticated(HttpMethod.Post, $"/api/monitored-devices/{device.Id}/checks");
        request.Content = JsonContent.Create(new
        {
            type = "Snmp",
            name = "SNMP: CPU",
            intervalSeconds = 60,
            timeoutSeconds = 5,
            parameters = new Dictionary<string, string> { ["oid"] = "1.3.6.1.2.1.1.3.0", ["metric"] = "cpu" },
            alertTuning = new { sustainedCycles = 0 },
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("AlertTuning.SustainedCycles", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCheck_WithAHysteresisOfOneHundredPercent_ReturnsValidationProblem()
    {
        var device = await CreateDeviceAsync();

        using var request = Authenticated(HttpMethod.Post, $"/api/monitored-devices/{device.Id}/checks");
        request.Content = JsonContent.Create(new
        {
            type = "Icmp",
            name = "Reachability",
            intervalSeconds = 60,
            timeoutSeconds = 5,
            alertTuning = new { hysteresisPercent = 100 },
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("AlertTuning.HysteresisPercent", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Telemetry naming a check that has been deleted since the poll. One unknown check must not fail
    /// the batch — the same rule WP-3.4's ingestion follows for a deleted device.
    /// </summary>
    [Fact]
    public async Task Evaluate_TelemetryForAnUnknownCheck_IsIgnoredWithoutFailingTheBatch()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        var ghost = Guid.CreateVersion7();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await EvaluateAsync(new DeviceTelemetryReported(
                Guid.CreateVersion7(), Now(cycle), "poller-1", "default", cycle,
                [
                    Reading(fixture, ghost, Now(cycle), 95),
                    Reading(fixture, fixture.CheckId, Now(cycle), 95),
                ]));
        }

        // The real check still alerted; the ghost produced nothing.
        var alerts = await AlertsAsync(fixture.DeviceId);
        Assert.All(alerts, alert => Assert.Equal(fixture.CheckId, alert.CheckId));
        Assert.Contains(alerts, alert => alert.MetricName == "cpu.utilisation_percent");
    }

    /// <summary>A disabled check is configuration that is switched off, so it stops alerting too.</summary>
    [Fact]
    public async Task Evaluate_ForADisabledCheck_RaisesNothing()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        await DisableCheckAsync(fixture);

        await DriveAsync(fixture, [95, 95, 95, 95]);

        Assert.Empty(await AlertsAsync(fixture.DeviceId));
    }

    /// <summary>An empty batch is a poller with nothing to say, not an error.</summary>
    [Fact]
    public async Task Evaluate_AnEmptyBatch_ChangesNothing()
    {
        var changed = await EvaluateAsync(new DeviceTelemetryReported(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, "poller-1", "default", 1, []));

        Assert.Equal(0, changed);
    }

    // ---- WP-3.7: the CMDB context an alert carries ----

    /// <summary>
    /// The WP's own verification, on the alert side: an alert on a switch says who holds it and where
    /// it is. The audit entry is where it lands durably — the alert row reads its CI live, and would
    /// answer differently once the asset is reassigned, so the dated record is the one that carries it.
    /// </summary>
    [Fact]
    public async Task Evaluate_RaisingAnAlertOnAnOwnedCi_RecordsItsOwnerLocationAndWarrantyInTheAudit()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        await GiveTheCiAnOwnerAsync(fixture.CiId, warrantyInDays: 9);

        await DriveAsync(fixture, [95, 95, 95]);

        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        var audit = await AuditForAlertAsync(alert.Id, "AlertRaised");
        Assert.Contains("Rosalind Frey", audit, StringComparison.Ordinal);
        Assert.Contains("Head Office", audit, StringComparison.Ordinal);
        Assert.Contains("ExpiringSoon", audit, StringComparison.Ordinal);
        Assert.Contains("\"ciFound\":true", audit.Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    /// <summary>
    /// The service on its own: what an alert board will ask for. Owner, location, warranty and what is
    /// already being worked on for the same CI.
    /// </summary>
    [Fact]
    public async Task Describe_ForACiWithAnOwner_ReadsTheCmdbLive()
    {
        var ci = await CreateCiAsync();
        await GiveTheCiAnOwnerAsync(ci.Id, warrantyInDays: -3);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = await scope.ServiceProvider.GetRequiredService<IAlertEnrichmentService>()
            .DescribeAsync(ci.Id, CancellationToken.None);

        Assert.True(context.CiFound);
        Assert.Equal("Rosalind Frey", context.OwnerName);
        Assert.Equal("Head Office", context.SiteName);
        Assert.Equal("Expired", context.WarrantyStatus);
        Assert.Equal(-3, context.WarrantyDaysRemaining);
        Assert.Empty(context.OpenTickets);
        Assert.Contains("Rosalind Frey", context.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure path. Deleting a monitored CI is not blocked by anything (the WP-3.1 note), so the
    /// enrichment has to answer "not found" rather than throw and take the whole publication with it.
    /// </summary>
    [Fact]
    public async Task Describe_ForACiThatIsNotInTheCmdb_SaysSoRatherThanFailing()
    {
        await using var scope = _application.Services.CreateAsyncScope();

        var context = await scope.ServiceProvider.GetRequiredService<IAlertEnrichmentService>()
            .DescribeAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.False(context.CiFound);
        Assert.Null(context.OwnerName);
        Assert.Empty(context.OpenTickets);
        Assert.Equal("CI not found in the CMDB", context.Headline);
    }

    [Fact]
    public async Task Evaluate_WithNoTelemetry_Throws()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAlertEngine>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            engine.EvaluateAsync(null!, CancellationToken.None));
    }

    // ---- driving ----

    private sealed record DeviceFixture(Guid DeviceId, Guid CiId, Guid CheckId, string Address);

    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-1);

    private static DateTimeOffset Now(int cycle) => Base.AddMinutes(cycle);

    /// <summary>One cycle per reading, a minute apart, exactly as a check on a fixed interval runs.</summary>
    private async Task DriveAsync(DeviceFixture fixture, IReadOnlyList<double> readings)
    {
        for (var cycle = 0; cycle < readings.Count; cycle++)
        {
            await EvaluateAsync(new DeviceTelemetryReported(
                Guid.CreateVersion7(), Now(cycle), "poller-1", "default", cycle,
                [Reading(fixture, fixture.CheckId, Now(cycle), readings[cycle])]));
        }
    }

    private async Task DriveFailuresAsync(DeviceFixture fixture, int cycles)
    {
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            // Offset past anything DriveAsync produced, so a test that does both keeps its order.
            var observedAt = Now(cycle + 100);
            await EvaluateAsync(new DeviceTelemetryReported(
                Guid.CreateVersion7(), observedAt, "poller-1", "default", cycle,
                [
                    new DeviceCheckResult(
                        fixture.DeviceId, fixture.CiId, fixture.CheckId, "Snmp", "SNMP: CPU",
                        fixture.Address, observedAt, Succeeded: false, LatencyMs: null,
                        Error: "Timed out after 5s", Metrics: []),
                ]));
        }
    }

    private static DeviceCheckResult Reading(
        DeviceFixture fixture,
        Guid checkId,
        DateTimeOffset observedAt,
        double cpu) => new(
        fixture.DeviceId, fixture.CiId, checkId, "Snmp", "SNMP: CPU", fixture.Address, observedAt,
        Succeeded: true, LatencyMs: 4, Error: null,
        Metrics: [new MetricSample("cpu.utilisation_percent", cpu, null, "%")]);

    private async Task<int> EvaluateAsync(DeviceTelemetryReported telemetry)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAlertEngine>()
            .EvaluateAsync(telemetry, CancellationToken.None);
    }

    // ---- reading back ----

    private async Task<IReadOnlyList<Alert>> AlertsAsync(Guid deviceId, bool thresholdRuleOnly = false)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var query = context.Alerts.Where(alert => alert.DeviceId == deviceId);
        if (thresholdRuleOnly)
        {
            query = query.Where(alert => alert.MetricName != "check.success");
        }

        return await query.OrderBy(alert => alert.RaisedAt).ToListAsync();
    }

    /// <summary>
    /// What actually reached the transactional outbox. Asserting on the outbox rather than on a test
    /// consumer is the point: ARCHITECTURE §4 requires every publish to go through it, so a message
    /// that is not here was not published the way this platform publishes things.
    /// </summary>
    private async Task<IReadOnlyList<TEvent>> PublishedAsync<TEvent>(Guid deviceId, string ruleId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var name = typeof(TEvent).Name;
        var bodies = await context.Set<OutboxMessage>()
            .Where(message => message.MessageType!.Contains(name)
                && message.Body.Contains(deviceId.ToString())
                && message.Body.Contains(ruleId))
            .OrderBy(message => message.SequenceNumber)
            .Select(message => message.Body)
            .ToListAsync();

        return [.. bodies.Select(Deserialize<TEvent>)];
    }

    /// <summary>
    /// The envelope is a MassTransit one, so the event is under <c>message</c>. Read with the web
    /// naming policy the bus serialises with.
    /// </summary>
    private static TEvent Deserialize<TEvent>(string body)
    {
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("message");
        return JsonSerializer.Deserialize<TEvent>(
            message.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<RedisValue> ReadAlertStateAsync(Guid deviceId, string ruleId)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString);
        return await connection.GetDatabase().StringGetAsync($"monitoring:alert-state:{deviceId}:{ruleId}");
    }

    private async Task FlushAlertStateAsync(Guid deviceId, string ruleId)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString);
        // This rule's key only. A FLUSHALL would take the state of every other test in the collection
        // with it, which is the shared-fixture trap WP-3.2's notes record for the broker.
        await connection.GetDatabase().KeyDeleteAsync($"monitoring:alert-state:{deviceId}:{ruleId}");
    }

    // ---- fixtures ----

    private async Task<DeviceFixture> CreateDeviceWithCpuCheckAsync(int? sustainedCycles = null)
    {
        var device = await CreateDeviceAsync();

        using var request = Authenticated(HttpMethod.Post, $"/api/monitored-devices/{device.Id}/checks");
        request.Content = JsonContent.Create(new
        {
            type = "Snmp",
            name = "SNMP: CPU",
            intervalSeconds = 60,
            timeoutSeconds = 5,
            warningThreshold = 70d,
            criticalThreshold = 90d,
            comparison = "GreaterThan",
            parameters = new Dictionary<string, string>
            {
                ["oid"] = "1.3.6.1.2.1.25.3.3.1.2",
                ["metric"] = "cpu",
            },
            alertTuning = sustainedCycles is null ? null : new { sustainedCycles },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var check = await response.Content.ReadFromJsonAsync<CheckDto>();

        return new DeviceFixture(device.Id, device.CiId, check!.Id, device.Address);
    }

    private async Task DisableCheckAsync(DeviceFixture fixture)
    {
        // A check is addressed on its own once it exists — see MonitoredDeviceEndpoints.
        using var request = Authenticated(HttpMethod.Put, $"/api/checks/{fixture.CheckId}");
        request.Content = JsonContent.Create(new
        {
            name = "SNMP: CPU",
            intervalSeconds = 60,
            timeoutSeconds = 5,
            warningThreshold = 70d,
            criticalThreshold = 90d,
            comparison = "GreaterThan",
            parameters = new Dictionary<string, string>
            {
                ["oid"] = "1.3.6.1.2.1.25.3.3.1.2",
                ["metric"] = "cpu",
            },
            isEnabled = false,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task CreateMaintenanceWindowAsync(
        Guid deviceId,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/maintenance-windows");
        request.Content = JsonContent.Create(new
        {
            name = $"Change {Guid.NewGuid():N}"[..20],
            startsAt = startsAt ?? Base.AddHours(-1),
            endsAt = endsAt ?? DateTimeOffset.UtcNow.AddHours(4),
            appliesToAllDevices = false,
            deviceIds = new[] { deviceId },
            isActive = true,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<DeviceDto> CreateDeviceAsync()
    {
        var ci = await CreateCiAsync();
        using var request = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        request.Content = JsonContent.Create(new
        {
            ciId = ci.Id,
            address = "10.40.0.1",
            pollerGroup = $"group-{Guid.NewGuid():N}"[..20],
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<DeviceDto>(await response.Content.ReadFromJsonAsync<DeviceDto>());
    }

    /// <summary>
    /// Ownership, location and a warranty written straight onto the CI. The assignment and coverage
    /// endpoints exist (WP-2.2, WP-2.6) but this is a fixture, not an operator's edit — the WP-2.8
    /// seeder rule — and what is under test is the read port, not those endpoints.
    /// </summary>
    private async Task GiveTheCiAnOwnerAsync(Guid ciId, int warrantyInDays)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var ci = await context.Cis.SingleAsync(item => item.Id == ciId);
        ci.OwnerName = "Rosalind Frey";
        ci.SiteName = "Head Office";
        ci.DepartmentName = "Network Operations";
        ci.WarrantyExpiresAt = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(warrantyInDays);
        await context.SaveChangesAsync();
    }

    /// <summary>The audit entry this alert wrote, as JSON. Scoped to the alert so test order cannot reach it.</summary>
    private async Task<string> AuditForAlertAsync(Guid alertId, string action)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .AuditEntries
            .Where(item => item.EntityType == "Alert" && item.EntityId == alertId.ToString() && item.Action == action)
            .OrderByDescending(item => item.OccurredAt)
            .FirstAsync();
        return entry.AfterJson ?? string.Empty;
    }

    private async Task<CiDto> CreateCiAsync()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "NetworkDevice",
            name = $"Switch {Guid.NewGuid():N}",
            attributes = new Dictionary<string, string>
            {
                ["managementIp"] = "10.0.0.1",
                ["vendor"] = "Cisco",
                ["portCount"] = "48",
            },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private async Task<T> GetAsync<T>(string uri)
    {
        using var request = Authenticated(HttpMethod.Get, uri);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(AlertAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record DeviceDto(Guid Id, Guid CiId, string Address, string PollerGroup);

    private sealed record AlertTuningDto(
        int? SustainedCycles,
        int? RecoveryCycles,
        double? HysteresisPercent,
        int? FlapThreshold,
        int? FlapWindowSeconds);

    private sealed record CheckDto(Guid Id, string Name, AlertTuningDto? AlertTuning);

    private sealed class AlertApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _redisConnectionString;
        private readonly string _minioConnectionString;

        public AlertApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string redisConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _redisConnectionString = redisConnectionString;
            _minioConnectionString = minioConnectionString;
            // Both as environment variables as well as in-memory configuration: Aspire's
            // AddNpgsqlDataSource and AddRedisClient read the builder's configuration while the host
            // is being built, which is before WebApplicationFactory's ConfigureAppConfiguration
            // sources are in place. The in-memory values below still matter — they are what the rest
            // of the host reads — but on their own the Redis client has no endpoint.
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__redis", redisConnectionString);
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
                    ["Platform:ApplyMigrations"] = "false",
                    // The engine is driven directly here, so the messages stay in the outbox where
                    // this class can read them. Putting them through the broker as well would make
                    // every assertion depend on delivery timing, and would stand a second MassTransit
                    // host beside the one the bus tests own — the WP-3.2 trap.
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = AlertAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = AlertAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = AlertAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, AlertAuthenticationHandler>(
                        AlertAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__redis", null);
        }
    }

    private sealed class AlertAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "AlertTest";
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
                    new Claim(ClaimTypes.NameIdentifier, "alert-test-user-id"),
                    new Claim(ClaimTypes.Name, "alert-test-user"),
                    new Claim(ClaimTypes.Role, role),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
