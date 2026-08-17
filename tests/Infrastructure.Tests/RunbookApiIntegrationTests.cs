using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Contracts.Events;

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
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Runbooks;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// WP-5.6 end to end over the real host: register an allowlisted runbook, let a poller collect an
/// execution, report a result, and watch the refusals — the 403 for anything not allowlisted, the
/// 429 for a runbook that has run its allowance, and the policies that keep an operator and an agent
/// out of each other's half of the channel.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class RunbookApiIntegrationTests : IAsyncLifetime
{
    private const string PollerRole = "Poller";
    private const string RestartService = "restart-service";

    private readonly RunbookApplication _application;
    private HttpClient? _client;

    public RunbookApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new RunbookApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
        await ClearRunbooksAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- the allowlist ----

    /// <summary>
    /// The WP's second verification step, and the single most important test in this file: asking to
    /// execute something nobody allowlisted is 403, and the answer says plainly that there is no way
    /// to add one over the API.
    /// </summary>
    [Fact]
    public async Task Execute_ARunbookThatIsNotAllowlisted_IsForbidden()
    {
        var device = await CreateDeviceAsync("10.56.0.1");

        using var request = Authenticated(HttpMethod.Post, "/api/runbooks/rm-rf-slash/executions", "Admin");
        request.Content = JsonContent.Create(new { deviceId = device.Id });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("not in this platform's runbook catalogue", problem, StringComparison.Ordinal);
        Assert.Contains("no endpoint that accepts a script", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_ARunbookThatIsNotAllowlisted_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/runbooks", "Admin");
        request.Content = JsonContent.Create(new { key = "curl-anything" });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The catalogue is compiled in, so it is readable and has no writer. A POST to it is a route that
    /// does not exist — which is the strongest form the WP's "no free-text execution path" can take.
    /// </summary>
    [Fact]
    public async Task Catalogue_IsReadableAndHasNoWriter()
    {
        var catalogue = await GetAsync<List<CatalogueDto>>("/api/runbooks/catalogue");

        Assert.Contains(catalogue, entry => entry.Key == RestartService);
        Assert.Contains(
            Assert.Single(catalogue, entry => entry.Key == RestartService).Parameters,
            parameter => parameter.Name == "service" && parameter.IsRequired);

        using var write = Authenticated(HttpMethod.Post, "/api/runbooks/catalogue", "Admin");
        write.Content = JsonContent.Create(new { key = "anything" });
        using var response = await _client!.SendAsync(write);
        // 405 rather than 404: the path exists and is readable, and there is no verb that writes it.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---- the registry ----

    [Fact]
    public async Task Register_AnAllowlistedRunbook_StoresItWithItsSchemaAndAnAuditEntry()
    {
        var runbook = await RegisterAsync();

        Assert.Equal(RestartService, runbook.Key);
        Assert.Equal(1, runbook.Version);
        Assert.True(runbook.IsAllowlisted);
        Assert.Equal("service", Assert.Single(runbook.Parameters).Name);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Contains(
            await context.AuditEntries.ToListAsync(),
            entry => entry.EntityType == "Runbook"
                && entry.EntityId == runbook.Id.ToString()
                && entry.Action == "Registered");
    }

    [Fact]
    public async Task Register_TheSameRunbookTwice_IsAConflict()
    {
        await RegisterAsync();

        using var request = Authenticated(HttpMethod.Post, "/api/runbooks", "Admin");
        request.Content = JsonContent.Create(new { key = RestartService });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// "Versioned" in the sense an execution needs: every edit moves the number, and an execution
    /// carries the one it ran under. The audit trail is the history of what changed.
    /// </summary>
    [Fact]
    public async Task Update_ARunbook_BumpsItsVersion()
    {
        await RegisterAsync();

        using var request = Authenticated(HttpMethod.Put, $"/api/runbooks/{RestartService}", "Admin");
        request.Content = JsonContent.Create(new { timeoutSeconds = 90, isEnabled = true });
        using var response = await _client!.SendAsync(request);
        var updated = Assert.IsType<RunbookDto>(await response.Content.ReadFromJsonAsync<RunbookDto>());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, updated.Version);
        Assert.Equal(90, updated.TimeoutSeconds);
    }

    [Fact]
    public async Task Update_ARunbookWithATimeoutOverTheCeiling_IsRefused()
    {
        await RegisterAsync();

        using var request = Authenticated(HttpMethod.Put, $"/api/runbooks/{RestartService}", "Admin");
        request.Content = JsonContent.Create(new { timeoutSeconds = 100_000 });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Deleting a runbook that has run something is refused. Those rows are the record of what this
    /// platform did to real machines, and a registration is not a way to erase it.
    /// </summary>
    [Fact]
    public async Task Delete_ARunbookWithExecutions_IsRefusedAndSaysToDisableItInstead()
    {
        var runbook = await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.2");
        await ExecuteAsync(device.Id);

        using var request = Authenticated(
            HttpMethod.Delete, $"/api/runbooks/{RestartService}", "Admin");
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Disable it instead", problem, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, runbook.Id);
    }

    // ---- triggers ----

    [Fact]
    public async Task AddTrigger_ForAnAlertMetric_StoresItsValidatedParameters()
    {
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.3");

        var trigger = await AddTriggerAsync(device.Id);

        Assert.Equal("check.success", trigger.MetricName);
        Assert.Equal("Critical", trigger.MinimumSeverity);
        Assert.Equal("nginx", trigger.Parameters["service"]);
    }

    /// <summary>
    /// The failure path that keeps the allowlist honest at configuration time: a trigger carrying a
    /// parameter the runbook does not take, or one shaped like a command, is refused when it is
    /// written rather than when an alert fires it at three in the morning.
    /// </summary>
    [Theory]
    [InlineData("service", "nginx; rm -rf /")]
    [InlineData("command", "reboot")]
    public async Task AddTrigger_WithParametersTheRunbookDoesNotAccept_IsRefused(string name, string value)
    {
        await RegisterAsync();

        using var request = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/triggers", "Admin");
        request.Content = JsonContent.Create(new
        {
            metricName = "check.success",
            minimumSeverity = "Critical",
            parameters = new Dictionary<string, string> { [name] = value },
        });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The standing `Enum.TryParse` hazard, met for the fourth time and here as a security control:
    /// "5" parses to a severity nothing has a name for, and a trigger stored at one would match
    /// everything or nothing depending on the number.
    /// </summary>
    [Theory]
    [InlineData("5")]
    [InlineData("Ok")]
    [InlineData("Catastrophic")]
    public async Task AddTrigger_WithASeverityThatIsNotOne_IsRefused(string severity)
    {
        await RegisterAsync();

        using var request = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/triggers", "Admin");
        request.Content = JsonContent.Create(new
        {
            metricName = "check.success",
            minimumSeverity = severity,
            parameters = new Dictionary<string, string> { ["service"] = "nginx" },
        });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- the execution channel ----

    /// <summary>
    /// The WP's first verification step, over the wire: an operator asks, a poller collects it, runs
    /// it and reports — and what comes back is on the execution with an audit row behind it.
    /// </summary>
    [Fact]
    public async Task Execute_ThenCollectAndReport_RecordsTheResultAndAuditsIt()
    {
        var group = NewGroup();
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.4", group);
        var poller = await RegisterPollerAsync(group);

        var execution = await ExecuteAsync(device.Id);
        Assert.Equal("Pending", execution.Status);

        var dispatch = await GetAsync<DispatchDto>(
            $"/api/pollers/{poller.Name}/runbook-executions", PollerRole);
        var claimed = Assert.Single(dispatch.Executions);
        Assert.Equal(execution.Id, claimed.ExecutionId);
        Assert.Equal(RestartService, claimed.RunbookKey);
        Assert.Equal("nginx", claimed.Parameters["service"]);
        Assert.Equal("10.56.0.4", claimed.Address);

        var reported = await ReportAsync(poller.Name, execution.Id, "Succeeded", 0, "Restarted nginx.");
        Assert.Equal("Succeeded", reported.Status);
        Assert.Equal(0, reported.ExitCode);
        Assert.Equal("Restarted nginx.", reported.Output);
        Assert.Equal(poller.Name, reported.PollerName);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entries = await context.AuditEntries
            .Where(entry => entry.EntityId == execution.Id.ToString())
            .ToListAsync();
        Assert.Contains(entries, entry => entry.Action == "ExecutionRequested");
        Assert.Contains(entries, entry => entry.Action == "Succeeded");
    }

    /// <summary>
    /// The dispatch carries no credential and no command — only a key the agent already implements
    /// and the parameters an operator wrote. A secret reaching an agent through this channel would be
    /// a way round the vault, which WP-3.11 built precisely to stop.
    /// </summary>
    [Fact]
    public async Task Collect_CarriesNoCommandAndNoCredential()
    {
        var group = NewGroup();
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.5", group);
        var poller = await RegisterPollerAsync(group);
        await ExecuteAsync(device.Id);

        using var request = Authenticated(
            HttpMethod.Get, $"/api/pollers/{poller.Name}/runbook-executions", PollerRole);
        using var response = await _client!.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var forbidden in new[] { "command", "script", "credential", "secret", "systemctl" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A second fetch gets nothing: an execution is claimed once, and claiming marks it.</summary>
    [Fact]
    public async Task Collect_Twice_HandsTheSameExecutionOverOnlyOnce()
    {
        var group = NewGroup();
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.6", group);
        var poller = await RegisterPollerAsync(group);
        await ExecuteAsync(device.Id);

        var first = await GetAsync<DispatchDto>(
            $"/api/pollers/{poller.Name}/runbook-executions", PollerRole);
        var second = await GetAsync<DispatchDto>(
            $"/api/pollers/{poller.Name}/runbook-executions", PollerRole);

        Assert.Single(first.Executions);
        Assert.Empty(second.Executions);
    }

    /// <summary>A poller is handed its own group's work and nobody else's.</summary>
    [Fact]
    public async Task Collect_ForAnotherGroupsDevice_HandsOverNothing()
    {
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.7", NewGroup());
        var poller = await RegisterPollerAsync(NewGroup());
        await ExecuteAsync(device.Id);

        var dispatch = await GetAsync<DispatchDto>(
            $"/api/pollers/{poller.Name}/runbook-executions", PollerRole);

        Assert.Empty(dispatch.Executions);
    }

    // ---- refusals on the channel ----

    [Fact]
    public async Task Report_ForAnExecutionThisPollerDoesNotHold_IsNotFound()
    {
        var group = NewGroup();
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.8", group);
        var mine = await RegisterPollerAsync(group);
        var other = await RegisterPollerAsync(group);
        var execution = await ExecuteAsync(device.Id);
        await GetAsync<DispatchDto>($"/api/pollers/{mine.Name}/runbook-executions", PollerRole);

        using var request = Authenticated(
            HttpMethod.Post,
            $"/api/pollers/{other.Name}/runbook-executions/{execution.Id}/results",
            PollerRole);
        request.Content = JsonContent.Create(new { outcome = "Succeeded", exitCode = 0 });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The first terminal state is the true one. A second report is a conflict rather than an
    /// overwrite, and the agent reads that as "already recorded" and stops asking.
    /// </summary>
    [Fact]
    public async Task Report_Twice_IsAConflictRatherThanAnOverwrite()
    {
        var group = NewGroup();
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.9", group);
        var poller = await RegisterPollerAsync(group);
        var execution = await ExecuteAsync(device.Id);
        await GetAsync<DispatchDto>($"/api/pollers/{poller.Name}/runbook-executions", PollerRole);
        await ReportAsync(poller.Name, execution.Id, "Succeeded", 0, "Restarted nginx.");

        using var request = Authenticated(
            HttpMethod.Post,
            $"/api/pollers/{poller.Name}/runbook-executions/{execution.Id}/results",
            PollerRole);
        request.Content = JsonContent.Create(new { outcome = "Failed", exitCode = 1 });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var after = await GetAsync<ExecutionDto>($"/api/runbook-executions/{execution.Id}", "Admin");
        Assert.Equal("Succeeded", after.Status);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Dispatched")]
    [InlineData("3")]
    [InlineData("Fine")]
    public async Task Report_WithAnOutcomeThatIsNotATerminalOne_IsRefused(string outcome)
    {
        var group = NewGroup();
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.10", group);
        var poller = await RegisterPollerAsync(group);
        var execution = await ExecuteAsync(device.Id);
        await GetAsync<DispatchDto>($"/api/pollers/{poller.Name}/runbook-executions", PollerRole);

        using var request = Authenticated(
            HttpMethod.Post,
            $"/api/pollers/{poller.Name}/runbook-executions/{execution.Id}/results",
            PollerRole);
        request.Content = JsonContent.Create(new { outcome });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- the bounds ----

    /// <summary>
    /// The per-runbook rate limit, counted from the table so it survives a Redis flush. The third
    /// request is refused with 429 and nothing is created.
    /// </summary>
    [Fact]
    public async Task Execute_PastTheRunbooksAllowance_IsRateLimited()
    {
        await RegisterAsync(maxExecutionsPerWindow: 2);
        var device = await CreateDeviceAsync("10.56.0.11");

        await ExecuteAsync(device.Id);
        await ExecuteAsync(device.Id);

        using var request = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/executions", "Admin");
        request.Content = JsonContent.Create(new
        {
            deviceId = device.Id,
            parameters = new Dictionary<string, string> { ["service"] = "nginx" },
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("its limit is 2", problem, StringComparison.Ordinal);

        var listed = await GetAsync<ExecutionPageDto>(
            $"/api/runbook-executions?deviceId={device.Id}", "Admin");
        Assert.Equal(2, listed.Total);
    }

    /// <summary>A refused execution is audited, because "the platform declined to touch a machine" is a fact an incident review looks for.</summary>
    [Fact]
    public async Task Execute_WhenRefused_IsAudited()
    {
        var runbook = await RegisterAsync(maxExecutionsPerWindow: 1);
        var device = await CreateDeviceAsync("10.56.0.12");
        await ExecuteAsync(device.Id);

        using var request = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/executions", "Admin");
        request.Content = JsonContent.Create(new
        {
            deviceId = device.Id,
            parameters = new Dictionary<string, string> { ["service"] = "nginx" },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Contains(
            await context.AuditEntries.Where(entry => entry.EntityType == "Runbook").ToListAsync(),
            entry => entry.EntityId == runbook.Id.ToString() && entry.Action == "ExecutionRefused");
    }

    [Fact]
    public async Task Execute_ADisabledRunbook_IsAConflict()
    {
        await RegisterAsync(isEnabled: false);
        var device = await CreateDeviceAsync("10.56.0.13");

        using var request = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/executions", "Admin");
        request.Content = JsonContent.Create(new
        {
            deviceId = device.Id,
            parameters = new Dictionary<string, string> { ["service"] = "nginx" },
        });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Execute_WithParametersTheRunbookDoesNotAccept_IsRefused()
    {
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.14");

        using var request = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/executions", "Admin");
        request.Content = JsonContent.Create(new
        {
            deviceId = device.Id,
            parameters = new Dictionary<string, string> { ["service"] = "nginx && reboot" },
        });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- the timeout ----

    /// <summary>
    /// The platform's own clock, not the agent's: an execution nobody ever reported on is finished by
    /// the sweeper, escalated like a failure, and never re-dispatched.
    /// </summary>
    [Fact]
    public async Task Sweep_AnExecutionPastItsDeadline_TimesItOutAndNeverHandsItOverAgain()
    {
        var group = NewGroup();
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.15", group);
        var poller = await RegisterPollerAsync(group);
        var execution = await ExecuteAsync(device.Id);
        await GetAsync<DispatchDto>($"/api/pollers/{poller.Name}/runbook-executions", PollerRole);

        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            var row = await context.RunbookExecutions.SingleAsync(item => item.Id == execution.Id);
            row.DeadlineAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();
        }

        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var swept = await scope.ServiceProvider
                .GetRequiredService<IRunbookTimeoutSweeper>().SweepAsync(default);
            Assert.True(swept >= 1);
        }

        var after = await GetAsync<ExecutionDto>($"/api/runbook-executions/{execution.Id}", "Admin");
        Assert.Equal("TimedOut", after.Status);
        Assert.Null(after.ExitCode);
        Assert.Contains("nothing was retried", after.Error!, StringComparison.OrdinalIgnoreCase);

        var again = await GetAsync<DispatchDto>(
            $"/api/pollers/{poller.Name}/runbook-executions", PollerRole);
        Assert.Empty(again.Executions);
    }

    // ---- triggers firing ----

    /// <summary>
    /// The automation itself: a raised alert matching a trigger creates exactly one execution, and a
    /// second raise of the same alert — an escalation, or a redelivered event — creates none.
    /// </summary>
    [Fact]
    public async Task Trigger_AMatchingAlert_StartsOneExecutionAndOnlyOne()
    {
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.16");
        await AddTriggerAsync(device.Id);
        var alert = AlertOn(device.Id, "Critical");

        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRunbookExecutionService>();
            Assert.Equal(1, await service.TriggerAsync(alert, default));
            Assert.Equal(0, await service.TriggerAsync(alert with { EventId = Guid.CreateVersion7() }, default));
        }

        var listed = await GetAsync<ExecutionPageDto>(
            $"/api/runbook-executions?deviceId={device.Id}", "Admin");
        var only = Assert.Single(listed.Items);
        Assert.Equal(alert.AlertId, only.AlertId);
        Assert.Equal("system:monitoring", only.RequestedBy);
        Assert.Equal("nginx", only.Parameters["service"]);
    }

    [Fact]
    public async Task Trigger_AnAlertBelowTheTriggersSeverity_StartsNothing()
    {
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.17");
        await AddTriggerAsync(device.Id);

        await using var scope = _application.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IRunbookExecutionService>();

        Assert.Equal(0, await service.TriggerAsync(AlertOn(device.Id, "Warning"), default));
    }

    /// <summary>
    /// A trigger scoped to one device is scoped to one device. It matters because the wider setting —
    /// every device in the estate — is the one that arms auto-remediation everywhere at once.
    /// </summary>
    [Fact]
    public async Task Trigger_AnAlertOnAnotherDevice_StartsNothing()
    {
        await RegisterAsync();
        var scoped = await CreateDeviceAsync("10.56.0.18");
        var other = await CreateDeviceAsync("10.56.0.19");
        await AddTriggerAsync(scoped.Id);

        await using var scope = _application.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IRunbookExecutionService>();

        Assert.Equal(0, await service.TriggerAsync(AlertOn(other.Id, "Critical"), default));
    }

    [Fact]
    public async Task Trigger_WhenTheTriggerIsDisabled_StartsNothing()
    {
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.20");
        var trigger = await AddTriggerAsync(device.Id);

        using var edit = Authenticated(
            HttpMethod.Put, $"/api/runbooks/{RestartService}/triggers/{trigger.Id}", "Admin");
        edit.Content = JsonContent.Create(new
        {
            metricName = "check.success",
            minimumSeverity = "Critical",
            deviceId = device.Id,
            parameters = new Dictionary<string, string> { ["service"] = "nginx" },
            isEnabled = false,
        });
        using var edited = await _client!.SendAsync(edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IRunbookExecutionService>();

        Assert.Equal(0, await service.TriggerAsync(AlertOn(device.Id, "Critical"), default));
    }

    // ---- who may do what ----

    /// <summary>
    /// The policy split ARCHITECTURE §6 requires. A Manager configures monitoring but does not run
    /// things on machines; an EndUser is nowhere near it; and administering the allowlist is narrower
    /// still than running one.
    /// </summary>
    [Theory]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("EndUser", HttpStatusCode.Forbidden)]
    [InlineData("Technician", HttpStatusCode.OK)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task Executions_AreReadableOnlyByWhoMayRunThem(string role, HttpStatusCode expected)
    {
        using var request = Authenticated(HttpMethod.Get, "/api/runbook-executions", role);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Registry_IsAdministeredByAdminsOnly()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/runbooks", "Technician");
        request.Content = JsonContent.Create(new { key = RestartService });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The two halves of the channel are disjoint, and this is both directions of it: an operator
    /// cannot collect an execution, and the poller's own credential cannot ask for one. Neither can
    /// do the other's half, which is what stops the channel becoming a way to run something.
    /// </summary>
    [Fact]
    public async Task Channel_IsClosedToOperators_AndTheAgentCannotRequestAnExecution()
    {
        var group = NewGroup();
        await RegisterAsync();
        var device = await CreateDeviceAsync("10.56.0.21", group);
        var poller = await RegisterPollerAsync(group);

        using var collect = Authenticated(
            HttpMethod.Get, $"/api/pollers/{poller.Name}/runbook-executions", "Admin");
        using var collected = await _client!.SendAsync(collect);
        Assert.Equal(HttpStatusCode.Forbidden, collected.StatusCode);

        using var run = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/executions", PollerRole);
        run.Content = JsonContent.Create(new { deviceId = device.Id });
        using var ran = await _client!.SendAsync(run);
        Assert.Equal(HttpStatusCode.Forbidden, ran.StatusCode);
    }

    // ---- fixtures ----

    private static AlertRaised AlertOn(Guid deviceId, string severity) => new(
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        Guid.CreateVersion7(),
        deviceId,
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        $"check:{Guid.CreateVersion7()}:availability",
        "Portal page",
        severity,
        "check.success",
        0,
        null,
        "Portal page on http-target is failing.",
        DateTimeOffset.UtcNow,
        3);

    /// <summary>
    /// The runbook tables are cleared per class rather than per test, so that one class's registration
    /// — there can only ever be one row per catalogue key — does not leak into the next run of it.
    /// </summary>
    private async Task ClearRunbooksAsync()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        await context.RunbookExecutions.ExecuteDeleteAsync();
        await context.RunbookTriggers.ExecuteDeleteAsync();
        await context.Runbooks.ExecuteDeleteAsync();
    }

    private async Task<RunbookDto> RegisterAsync(
        int? maxExecutionsPerWindow = null,
        bool isEnabled = true)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/runbooks", "Admin");
        request.Content = JsonContent.Create(new
        {
            key = RestartService,
            maxExecutionsPerWindow,
            isEnabled,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<RunbookDto>(await response.Content.ReadFromJsonAsync<RunbookDto>());
    }

    private async Task<TriggerDto> AddTriggerAsync(Guid deviceId)
    {
        using var request = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/triggers", "Admin");
        request.Content = JsonContent.Create(new
        {
            metricName = "check.success",
            minimumSeverity = "Critical",
            deviceId,
            parameters = new Dictionary<string, string> { ["service"] = "nginx" },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TriggerDto>(await response.Content.ReadFromJsonAsync<TriggerDto>());
    }

    private async Task<ExecutionDto> ExecuteAsync(Guid deviceId)
    {
        using var request = Authenticated(
            HttpMethod.Post, $"/api/runbooks/{RestartService}/executions", "Admin");
        request.Content = JsonContent.Create(new
        {
            deviceId,
            parameters = new Dictionary<string, string> { ["service"] = "nginx" },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<ExecutionDto>(await response.Content.ReadFromJsonAsync<ExecutionDto>());
    }

    private async Task<ExecutionDto> ReportAsync(
        string pollerName,
        Guid executionId,
        string outcome,
        int? exitCode,
        string? output)
    {
        using var request = Authenticated(
            HttpMethod.Post,
            $"/api/pollers/{pollerName}/runbook-executions/{executionId}/results",
            PollerRole);
        request.Content = JsonContent.Create(new { outcome, exitCode, output });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ExecutionDto>(await response.Content.ReadFromJsonAsync<ExecutionDto>());
    }

    private async Task<DeviceDto> CreateDeviceAsync(string address, string? pollerGroup = null)
    {
        var ci = await CreateCiAsync();
        using var request = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        request.Content = JsonContent.Create(new
        {
            ciId = ci.Id,
            address,
            pollerGroup = pollerGroup ?? NewGroup(),
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<DeviceDto>(await response.Content.ReadFromJsonAsync<DeviceDto>());
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

    private async Task<PollerDto> RegisterPollerAsync(string pollerGroup)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/pollers/registrations", PollerRole);
        request.Content = JsonContent.Create(new
        {
            name = $"poller-{Guid.NewGuid():N}"[..20],
            pollerGroup,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<PollerDto>(await response.Content.ReadFromJsonAsync<PollerDto>());
    }

    private async Task<T> GetAsync<T>(string uri, string role = "Technician")
    {
        using var request = Authenticated(HttpMethod.Get, uri, role);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static string NewGroup() => $"group-{Guid.NewGuid():N}"[..20];

    private static HttpRequestMessage Authenticated(
        HttpMethod method,
        string uri,
        string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(RunbookAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record DeviceDto(Guid Id, Guid CiId, string Address, string PollerGroup);

    private sealed record PollerDto(Guid Id, string Name, string PollerGroup);

    private sealed record CatalogueParameterDto(string Name, string Description, bool IsRequired);

    private sealed record CatalogueDto(
        string Key,
        string Name,
        string Description,
        int DefaultTimeoutSeconds,
        List<CatalogueParameterDto> Parameters);

    private sealed record RunbookParameterDto(string Name, bool IsRequired, int MaxLength);

    private sealed record RunbookDto(
        Guid Id,
        string Key,
        string Name,
        int Version,
        int TimeoutSeconds,
        int MaxExecutionsPerWindow,
        bool IsEnabled,
        bool IsAllowlisted,
        List<RunbookParameterDto> Parameters,
        List<TriggerDto> Triggers);

    private sealed record TriggerDto(
        Guid Id,
        Guid RunbookId,
        string MetricName,
        string MinimumSeverity,
        Guid? DeviceId,
        Dictionary<string, string> Parameters,
        bool IsEnabled);

    private sealed record ExecutionDto(
        Guid Id,
        string RunbookKey,
        int RunbookVersion,
        Guid? AlertId,
        Guid DeviceId,
        Dictionary<string, string> Parameters,
        string Status,
        string RequestedBy,
        string? PollerName,
        int? ExitCode,
        string? Output,
        string? Error);

    private sealed record ExecutionPageDto(List<ExecutionDto> Items, int Total, int Page, int PageSize);

    private sealed record DispatchItemDto(
        Guid ExecutionId,
        string RunbookKey,
        int RunbookVersion,
        Guid DeviceId,
        string Address,
        Dictionary<string, string> Parameters,
        int TimeoutSeconds);

    private sealed record DispatchDto(
        string PollerName,
        string PollerGroup,
        List<DispatchItemDto> Executions);

    private sealed class RunbookApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public RunbookApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", rabbitMqConnectionString);
            Environment.SetEnvironmentVariable("Platform__EnableMessageBus", "true");
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
                    ["ConnectionStrings:minio"] = _minioConnectionString,
                    ["ObjectStorage:AccessKey"] = "minioadmin",
                    ["ObjectStorage:SecretKey"] = "minio-test-password",
                    ["Platform:ApplyMigrations"] = "false",
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = RunbookAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = RunbookAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = RunbookAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, RunbookAuthenticationHandler>(
                        RunbookAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", null);
            Environment.SetEnvironmentVariable("Platform__EnableMessageBus", null);
        }
    }

    private sealed class RunbookAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "RunbookTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, $"runbook-test-{role}"),
                    new Claim(ClaimTypes.Name, "runbook-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
