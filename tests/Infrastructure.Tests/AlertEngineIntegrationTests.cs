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
using Modules.Monitoring.Features.MaintenanceWindows;

using Platform.Data;
using Platform.Integration;

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
    private readonly InfrastructureFixture _infrastructure;
    private readonly string _redisConnectionString;
    private HttpClient? _client;

    public AlertEngineIntegrationTests(InfrastructureFixture infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        _infrastructure = infrastructure;
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

    // ---- WP-5.8: the window an approved change opens ----

    /// <summary>
    /// The sync half of WP-5.8 against a real database: an approval names CIs, and the window that opens
    /// covers the monitored devices those CIs are — not the CIs, and not the estate.
    /// </summary>
    [Fact]
    public async Task Sync_ForAnApprovedChange_OpensAWindowScopedToTheDevicesThoseCisAre()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        var changeId = Guid.CreateVersion7();

        await SyncApprovalAsync(changeId, [fixture.CiId]);

        var window = await WindowForChangeAsync(changeId);
        Assert.NotNull(window);
        Assert.False(window.AppliesToAllDevices);
        Assert.Equal(fixture.DeviceId, Assert.Single(window.Devices).DeviceId);
        Assert.Contains("CHG-", window.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// A redelivered approval opens no second window. The filtered unique index is the guarantee; this
    /// asserts the service reaches the same answer without relying on a constraint violation to do it.
    /// </summary>
    [Fact]
    public async Task Sync_DeliveredTwice_OpensExactlyOneWindow()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        var changeId = Guid.CreateVersion7();

        await SyncApprovalAsync(changeId, [fixture.CiId]);
        await SyncApprovalAsync(changeId, [fixture.CiId]);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        Assert.Equal(1, await context.MaintenanceWindows.CountAsync(w => w.ChangeRequestId == changeId));
    }

    /// <summary>
    /// The failure path that matters most, because getting it wrong is silent and estate-wide: a change
    /// covering CIs nothing polls must open <em>no</em> window, never a window over everything.
    /// </summary>
    [Fact]
    public async Task Sync_ForCisNothingMonitors_OpensNoWindowAtAll()
    {
        var ci = await CreateCiAsync();
        var changeId = Guid.CreateVersion7();

        await SyncApprovalAsync(changeId, [ci.Id]);

        Assert.Null(await WindowForChangeAsync(changeId));

        // And nothing estate-wide was created as a side effect, which is the shape of the mistake.
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        Assert.False(await context.MaintenanceWindows
            .AnyAsync(window => window.ChangeRequestId == changeId || window.AppliesToAllDevices));
    }

    /// <summary>
    /// The WP's own verification step, driven end to end: approve maintenance on the device, then break
    /// it. The alert is recorded, and nobody is told — so no ticket is opened either, because WP-3.6's
    /// automation consumes the event this did not publish.
    /// </summary>
    [Fact]
    public async Task Evaluate_InsideAWindowAnApprovedChangeOpened_RecordsTheAlertAndPublishesNothing()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        await SyncApprovalAsync(Guid.CreateVersion7(), [fixture.CiId]);

        await DriveAsync(fixture, [95, 95, 95, 95]);

        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(AlertSuppression.Maintenance, alert.Suppression);
        Assert.Empty(await PublishedAsync<AlertRaised>(fixture.DeviceId, alert.RuleId));
    }

    /// <summary>
    /// The half of the WP's verification that says "prove it": once the approved change's window ends,
    /// the same still-broken device alerts, and it alerts exactly once rather than for every muted cycle
    /// it sat through. The alert row is the same one throughout — nothing was re-raised.
    /// </summary>
    [Fact]
    public async Task Evaluate_AfterTheApprovedChangesWindowEnds_PublishesTheAlertOnce()
    {
        var fixture = await CreateDeviceWithCpuCheckAsync();
        var changeId = Guid.CreateVersion7();
        await SyncApprovalAsync(changeId, [fixture.CiId]);

        await DriveAsync(fixture, [95, 95, 95, 95]);
        var muted = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Empty(await PublishedAsync<AlertRaised>(fixture.DeviceId, muted.RuleId));

        // The slot ends at cycle 5: the four muted readings above are inside it, the two below are not.
        await EndWindowAtCycleAsync(changeId, atCycle: 5);
        await DriveBatchAsync([(fixture, 96d)], cycles: 2, startingCycle: 10);

        var alert = Assert.Single(await AlertsAsync(fixture.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(muted.Id, alert.Id);
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

    // ---- root-cause suppression (WP-5.1) ----

    /// <summary>
    /// The WP's headline verification step, end to end: a switch and three CIs that depend on it all
    /// stop answering in the same cycle. Exactly one alert is published — the switch's — and the three
    /// consequences are recorded, suppressed and filed under it.
    /// <para>
    /// All four cross their sustain count on the same reading, which is the case that makes inline
    /// correlation necessary: at the top of that cycle no alert row exists for any of them, so a
    /// correlator that read only committed state would find nothing to suppress under.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Evaluate_WhenASwitchAndItsDependentsFailTogether_PublishesOnlyTheSwitch()
    {
        var estate = await CreateDependencyTreeAsync(dependents: 3);

        await DriveEstateAsync(estate, cycles: 3, cpu: 95);

        // One publication for the whole outage.
        var raised = await PublishedForEstateAsync<AlertRaised>(estate);
        var cause = Assert.Single(raised);
        Assert.Equal(estate.Root.DeviceId, cause.DeviceId);

        // …and four alert rows, because suppression withholds the message and never the record.
        var causeAlert = Assert.Single(await AlertsAsync(estate.Root.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertSuppression.None, causeAlert.Suppression);
        Assert.Null(causeAlert.RootCauseAlertId);

        foreach (var dependent in estate.Dependents)
        {
            var alert = Assert.Single(await AlertsAsync(dependent.DeviceId, thresholdRuleOnly: true));
            Assert.Equal(AlertStatus.Open, alert.Status);
            Assert.Equal(AlertSeverity.Critical, alert.Severity);
            Assert.Equal(AlertSuppression.RootCause, alert.Suppression);
            Assert.Equal(causeAlert.Id, alert.RootCauseAlertId);
        }
    }

    /// <summary>
    /// "Stop a leaf only → normal single alert path unaffected." Nothing this CI depends on is failing,
    /// so the correlator has nothing to say and the alert travels exactly as it did before WP-5.1.
    /// </summary>
    [Fact]
    public async Task Evaluate_WhenOnlyADependentFails_PublishesItNormally()
    {
        var estate = await CreateDependencyTreeAsync(dependents: 1);
        var leaf = estate.Dependents[0];

        await DriveAsync(leaf, [95, 95, 95]);

        var alert = Assert.Single(await AlertsAsync(leaf.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertSuppression.None, alert.Suppression);
        Assert.Null(alert.RootCauseAlertId);
        Assert.Single(await PublishedAsync<AlertRaised>(
            leaf.DeviceId, $"check:{leaf.CheckId}:cpu.utilisation_percent"));
    }

    /// <summary>
    /// "Revive → all clear." The switch recovers and takes its dependents with it, and the whole
    /// outage closes having published one raise and one clear — the three suppressed alerts announce
    /// neither, because nobody was ever told they had started.
    /// </summary>
    [Fact]
    public async Task Evaluate_WhenTheSwitchRecovers_ClearsTheCauseAndSaysNothingAboutTheConsequences()
    {
        var estate = await CreateDependencyTreeAsync(dependents: 2);
        await DriveEstateAsync(estate, cycles: 3, cpu: 95);

        await DriveEstateAsync(estate, cycles: 2, cpu: 10, startingCycle: 3);

        var cleared = Assert.Single(await PublishedForEstateAsync<AlertCleared>(estate));
        Assert.Equal(estate.Root.DeviceId, cleared.DeviceId);

        foreach (var dependent in estate.Dependents)
        {
            // The row closed quietly, which is what a suppressed alert recovering has to do.
            var alert = Assert.Single(await AlertsAsync(dependent.DeviceId, thresholdRuleOnly: true));
            Assert.Equal(AlertStatus.Cleared, alert.Status);
            Assert.Null(alert.RootCauseAlertId);
        }
    }

    /// <summary>
    /// The half of the recovery that would be easy to get wrong: the cause is fixed and a consequence
    /// is not. It was never really a consequence, so it has to speak for itself the moment the
    /// explanation goes away — an alert suppressed under a cause that has recovered would be an outage
    /// nobody was ever told about.
    /// </summary>
    [Fact]
    public async Task Evaluate_WhenTheCauseRecoversAndADependentDoesNot_PublishesTheDependentThen()
    {
        var estate = await CreateDependencyTreeAsync(dependents: 1);
        var stubborn = estate.Dependents[0];
        await DriveEstateAsync(estate, cycles: 3, cpu: 95);
        Assert.Empty(await PublishedAsync<AlertRaised>(
            stubborn.DeviceId, $"check:{stubborn.CheckId}:cpu.utilisation_percent"));

        // The switch is healthy again; the server behind it is still at 95%.
        await DriveBatchAsync(
            [(estate.Root, 10d), (stubborn, 95d)], cycles: 3, startingCycle: 3);

        Assert.Single(await PublishedAsync<AlertRaised>(
            stubborn.DeviceId, $"check:{stubborn.CheckId}:cpu.utilisation_percent"));
        var alert = Assert.Single(await AlertsAsync(stubborn.DeviceId, thresholdRuleOnly: true));
        Assert.Equal(AlertSuppression.None, alert.Suppression);
        Assert.Null(alert.RootCauseAlertId);
    }

    /// <summary>
    /// The failure path, and the property the whole feature is built around: two CIs that depend on
    /// each other explain each other, so neither can be named as the cause and <em>neither is
    /// silenced</em>. A clustered pair is a real estate shape (WP-2.3 accepts cycles deliberately), and
    /// two tickets is the correct answer — nought would be an outage nothing reported.
    /// </summary>
    [Fact]
    public async Task Evaluate_WhenTwoFailingCisDependOnEachOther_PublishesBoth()
    {
        var first = await CreateDeviceWithCpuCheckAsync();
        var second = await CreateDeviceWithCpuCheckAsync();
        await RelateAsync(first.CiId, second.CiId);
        await RelateAsync(second.CiId, first.CiId);

        await DriveBatchAsync([(first, 95d), (second, 95d)], cycles: 3);

        Assert.Single(await PublishedAsync<AlertRaised>(
            first.DeviceId, $"check:{first.CheckId}:cpu.utilisation_percent"));
        Assert.Single(await PublishedAsync<AlertRaised>(
            second.DeviceId, $"check:{second.CheckId}:cpu.utilisation_percent"));
        Assert.All(
            await AlertsAsync(first.DeviceId, thresholdRuleOnly: true),
            alert => Assert.Equal(AlertSuppression.None, alert.Suppression));
    }

    /// <summary>
    /// Correlation reads the CMDB through a port, and a CI with no relationships at all is the normal
    /// case on most estates. Two unrelated devices failing at once are two incidents and two tickets.
    /// </summary>
    [Fact]
    public async Task Evaluate_WhenTwoUnrelatedDevicesFail_PublishesBoth()
    {
        var first = await CreateDeviceWithCpuCheckAsync();
        var second = await CreateDeviceWithCpuCheckAsync();

        await DriveBatchAsync([(first, 95d), (second, 95d)], cycles: 3);

        Assert.Single(await PublishedAsync<AlertRaised>(
            first.DeviceId, $"check:{first.CheckId}:cpu.utilisation_percent"));
        Assert.Single(await PublishedAsync<AlertRaised>(
            second.DeviceId, $"check:{second.CheckId}:cpu.utilisation_percent"));
    }

    /// <summary>
    /// The kill switch, on a host of its own. This is the setting to reach for if correlation is ever
    /// suspected of hiding something, so "off really means off" has to be a test rather than a claim:
    /// the same estate that produces one publication above produces two here, with no grouping
    /// recorded on either row.
    /// <para>
    /// Found missing by the hand-verification walk on 2026-08-14, which exercised the flag live and
    /// noticed nothing automated covered it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Evaluate_WithCorrelationDisabled_PublishesEveryAlertAndGroupsNothing()
    {
        var estate = await CreateDependencyTreeAsync(dependents: 1);
        await using var uncorrelated = new AlertApplication(
            _infrastructure.PostgresConnectionString,
            _infrastructure.RabbitMqConnectionString,
            _infrastructure.RedisConnectionString,
            _infrastructure.MinioConnectionString,
            new Dictionary<string, string?> { ["Monitoring:Alerting:CorrelationEnabled"] = "false" });

        await DriveBatchAsync(
            [.. estate.All.Select(device => (device, 95d))], cycles: 3, host: uncorrelated);

        foreach (var device in estate.All)
        {
            var alert = Assert.Single(await AlertsAsync(device.DeviceId, thresholdRuleOnly: true));
            Assert.Equal(AlertSuppression.None, alert.Suppression);
            Assert.Null(alert.RootCauseAlertId);
            Assert.Single(await PublishedAsync<AlertRaised>(
                device.DeviceId, $"check:{device.CheckId}:cpu.utilisation_percent"));
        }
    }

    /// <summary>
    /// The other side of the port: what a root-cause ticket reads to list the CIs an outage took with
    /// it. Open alerts only, so a consequence that recovers drops off it.
    /// </summary>
    [Fact]
    public async Task ImpactedBy_ForARootCauseAlert_ListsWhatIsSuppressedUnderIt()
    {
        var estate = await CreateDependencyTreeAsync(dependents: 2);
        await DriveEstateAsync(estate, cycles: 3, cpu: 95);
        var cause = Assert.Single(await AlertsAsync(estate.Root.DeviceId, thresholdRuleOnly: true));

        await using var scope = _application.Services.CreateAsyncScope();
        var impacted = await scope.ServiceProvider.GetRequiredService<IAlertCorrelationDirectory>()
            .GetImpactedByAsync(cause.Id, CancellationToken.None);

        Assert.Equal(2, impacted.Count);
        Assert.Equal(
            [.. estate.Dependents.Select(dependent => dependent.CiId).Order()],
            [.. impacted.Select(entry => entry.CiId).Order()]);
        Assert.All(impacted, entry => Assert.Equal("Critical", entry.Severity));
    }

    /// <summary>
    /// The Assets side of the same arrangement, over a real graph: the port answers with both ends
    /// inside the set it was asked about and never with a healthy dependency, because the caller is
    /// only ever asking about things that are already broken.
    /// </summary>
    [Fact]
    public async Task Dependencies_AmongAFailingSet_ReportsOnlyThePairsInsideIt()
    {
        var switchCi = await CreateCiAsync();
        var host = await CreateCiAsync();
        var vm = await CreateCiAsync();
        var bystander = await CreateCiAsync();
        await RelateAsync(host.Id, switchCi.Id);
        await RelateAsync(vm.Id, host.Id);

        await using var scope = _application.Services.CreateAsyncScope();
        var links = await scope.ServiceProvider.GetRequiredService<ICiDependencyDirectory>()
            .GetDependenciesAmongAsync([switchCi.Id, host.Id, vm.Id], maxDepth: 5, CancellationToken.None);

        // host→switch, vm→host, and vm→switch two hops away: the walk is transitive, which is what
        // lets the correlator file a whole chain under its far end without walking it itself.
        Assert.Equal(3, links.Count);
        Assert.Contains(links, link => link.CiId == host.Id && link.DependsOnCiId == switchCi.Id && link.Depth == 1);
        Assert.Contains(links, link => link.CiId == vm.Id && link.DependsOnCiId == host.Id && link.Depth == 1);
        Assert.Contains(links, link => link.CiId == vm.Id && link.DependsOnCiId == switchCi.Id && link.Depth == 2);
        Assert.DoesNotContain(links, link => link.CiId == bystander.Id || link.DependsOnCiId == bystander.Id);
    }

    /// <summary>
    /// A set of one cannot contain a dependency, and the port says so without going to the database —
    /// which is the shape of almost every call, because almost every estate has one thing wrong at a
    /// time.
    /// </summary>
    [Fact]
    public async Task Dependencies_ForASingleCi_AnswersNothing()
    {
        var ci = await CreateCiAsync();

        await using var scope = _application.Services.CreateAsyncScope();
        var links = await scope.ServiceProvider.GetRequiredService<ICiDependencyDirectory>()
            .GetDependenciesAmongAsync([ci.Id], maxDepth: 5, CancellationToken.None);

        Assert.Empty(links);
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

    /// <summary>A cause and the devices whose CIs depend on it (WP-5.1).</summary>
    private sealed record EstateFixture(DeviceFixture Root, IReadOnlyList<DeviceFixture> Dependents)
    {
        public IEnumerable<DeviceFixture> All => [Root, .. Dependents];
    }

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

    /// <summary>
    /// A switch and the devices that depend on it: one CI per device, and a relationship pointing from
    /// each dependent to the switch, which by WP-2.3's convention is "this needs that".
    /// </summary>
    private async Task<EstateFixture> CreateDependencyTreeAsync(int dependents)
    {
        var root = await CreateDeviceWithCpuCheckAsync();
        var behind = new List<DeviceFixture>(dependents);
        for (var index = 0; index < dependents; index++)
        {
            var dependent = await CreateDeviceWithCpuCheckAsync();
            await RelateAsync(dependent.CiId, root.CiId);
            behind.Add(dependent);
        }

        return new EstateFixture(root, behind);
    }

    /// <summary>"<paramref name="sourceCiId"/> needs <paramref name="targetCiId"/>" — WP-2.3's direction.</summary>
    private async Task RelateAsync(Guid sourceCiId, Guid targetCiId)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/cis/{sourceCiId}/relationships");
        request.Content = JsonContent.Create(new { targetCiId, type = "DependsOn" });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private Task DriveEstateAsync(EstateFixture estate, int cycles, double cpu, int startingCycle = 0) =>
        DriveBatchAsync(
            [.. estate.All.Select(device => (device, cpu))], cycles, startingCycle);

    /// <summary>
    /// Several devices in <em>one</em> telemetry batch, which is what a poller actually publishes: one
    /// message per cycle carrying every device it polled (WP-3.3). Driving them as separate batches
    /// would be a different test — and an easier one, because the second batch would find the first
    /// device's alert already committed.
    /// </summary>
    private async Task DriveBatchAsync(
        IReadOnlyList<(DeviceFixture Device, double Cpu)> devices,
        int cycles,
        int startingCycle = 0,
        AlertApplication? host = null)
    {
        for (var cycle = startingCycle; cycle < startingCycle + cycles; cycle++)
        {
            var observedAt = Now(cycle);
            await EvaluateAsync(
                new DeviceTelemetryReported(
                    Guid.CreateVersion7(), observedAt, "poller-1", "default", cycle,
                    [.. devices.Select(entry => Reading(entry.Device, entry.Device.CheckId, observedAt, entry.Cpu))]),
                host);
        }
    }

    /// <summary>
    /// What this estate published, scoped to its own devices. The Platform outbox is shared by the
    /// whole collection, so counting messages globally would pass or fail on test order — the same
    /// trap the per-rule reads below avoid.
    /// </summary>
    private async Task<IReadOnlyList<TEvent>> PublishedForEstateAsync<TEvent>(EstateFixture estate)
        where TEvent : class
    {
        var published = new List<TEvent>();
        foreach (var device in estate.All)
        {
            published.AddRange(await PublishedAsync<TEvent>(
                device.DeviceId, $"check:{device.CheckId}:cpu.utilisation_percent"));
        }

        return published;
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

    private async Task<int> EvaluateAsync(DeviceTelemetryReported telemetry, AlertApplication? host = null)
    {
        await using var scope = (host ?? _application).Services.CreateAsyncScope();
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

    /// <summary>
    /// The Monitoring half of an approval, invoked directly rather than over the bus (WP-5.8).
    /// <para>
    /// That is the whole reason <c>MaintenanceSyncService</c> is split from its consumer, following
    /// WP-4.2: the consumer's job is idempotency and the service's job is the work. Driving it through
    /// MassTransit here would test delivery — which this repository already has a known-intermittent
    /// family of tests for — rather than the thing WP-5.8 is about.
    /// </para>
    /// </summary>
    /// <param name="endsAt">
    /// Defaults to well past every cycle this suite drives. It is expressed against <see cref="Base"/>
    /// and not against the wall clock on purpose: the engine compares a window to the <em>telemetry's</em>
    /// timestamp, and this suite's cycles are deliberately an hour in the past, so a window "starting
    /// five minutes ago" would cover none of them.
    /// </param>
    private async Task SyncApprovalAsync(
        Guid changeRequestId,
        IReadOnlyList<Guid> ciIds,
        DateTimeOffset? endsAt = null)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMaintenanceSyncService>().SyncAsync(
            new ChangeRequestApproved(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                changeRequestId,
                $"CHG-{Random.Shared.Next(1, 999_999):000000}",
                "Firmware upgrade",
                Base.AddHours(-1),
                endsAt ?? Base.AddHours(4),
                ciIds),
            CancellationToken.None);
    }

    private async Task<MaintenanceWindow?> WindowForChangeAsync(Guid changeRequestId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>()
            .MaintenanceWindows.AsNoTracking()
            .Include(window => window.Devices)
            .SingleOrDefaultAsync(window => window.ChangeRequestId == changeRequestId);
    }

    /// <summary>
    /// Ends the window at <paramref name="atCycle"/>, so cycles before it are inside the window and
    /// cycles after it are outside — which is what the clock does on its own, expressed in the units the
    /// engine actually compares against.
    /// <para>
    /// Written directly rather than through the API because a window is the platform's own record of an
    /// agreed change and no endpoint shortens one, and because a test that waited out a real maintenance
    /// slot is a test nobody runs.
    /// </para>
    /// </summary>
    private async Task EndWindowAtCycleAsync(Guid changeRequestId, int atCycle)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var window = await context.MaintenanceWindows
            .SingleAsync(item => item.ChangeRequestId == changeRequestId);
        window.EndsAt = Now(atCycle);
        await context.SaveChangesAsync();
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

    private static HttpRequestMessage Authenticated(
        HttpMethod method,
        string uri,
        string role = "Technician",
        string? actorId = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(AlertAuthenticationHandler.RoleHeader, role);
        if (actorId is not null)
        {
            request.Headers.Add(AlertAuthenticationHandler.ActorHeader, actorId);
        }

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

        /// <summary>
        /// Settings this host overrides on top of the defaults below — how a test spins a second host
        /// with the platform tuned differently, following `TopologyApiIntegrationTests`' node budget.
        /// </summary>
        private readonly IReadOnlyDictionary<string, string?> _overrides;

        public AlertApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string redisConnectionString,
            string minioConnectionString,
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _redisConnectionString = redisConnectionString;
            _minioConnectionString = minioConnectionString;
            _overrides = overrides ?? new Dictionary<string, string?>();
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
            // After the defaults, so a test can replace one of them.
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(_overrides));
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

        /// <summary>
        /// Optional, and it exists for exactly one rule: WP-5.8 refuses to let anybody approve their own
        /// change, so proving that needs two people rather than two roles.
        /// </summary>
        public const string ActorHeader = "X-Test-Actor";

        public const string DefaultActorId = "alert-test-user-id";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].ToString();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var actorId = Request.Headers[ActorHeader].ToString();
            if (string.IsNullOrWhiteSpace(actorId))
            {
                actorId = DefaultActorId;
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, actorId),
                    new Claim(ClaimTypes.Name, actorId),
                    new Claim(ClaimTypes.Role, role),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
