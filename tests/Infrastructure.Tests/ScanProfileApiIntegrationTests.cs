using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

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
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Discovery;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The WP's configuration half: scan profiles created and edited through the operator API, and read
/// back by the discovery service through its own.
/// <para>
/// Two audiences and two policies, which is the thing most worth guarding here. The operator surface
/// is <c>CanManageMonitoring</c>; the scanner's fetch is <c>CanDiscover</c>, which is the
/// <c>Discovery</c> realm role and nothing else — not Admin, and deliberately not <c>Poller</c>. That
/// separation is what keeps a stolen scanner token away from everything the credential vault protects.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ScanProfileApiIntegrationTests : IAsyncLifetime
{
    private const string DiscoveryRole = "Discovery";
    private const string PollerRole = "Poller";

    private readonly ScanProfileApplication _application;
    private HttpClient? _client;

    public ScanProfileApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new ScanProfileApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ScanProfile_Created_IsReadBackWithItsRangesPortsAndAddressCount()
    {
        var group = NewGroup();

        var created = await CreateAsync(NewName(), group, ["10.30.0.0/29", "192.0.2.1"], [22, 443]);

        Assert.Equal(group, created.DiscoveryGroup);
        Assert.Equal(["10.30.0.0/29", "192.0.2.1"], created.Ranges);
        Assert.Equal([22, 443], created.Ports);
        // Six hosts out of the /29 plus the single address. The figure exists so an operator can see
        // that a /16 they typed is 65,534 probes before the scanner tries them.
        Assert.Equal(7, created.AddressCount);
        Assert.True(created.SnmpEnabled);
        Assert.True(created.NeighbourDiscoveryEnabled);

        var fetched = await GetAsync<ScanProfileDto>($"/api/scan-profiles/{created.Id}");
        Assert.Equal(created.Id, fetched.Id);
    }

    /// <summary>
    /// The discovery service's own read, and the WP's "scan profiles configured via API" half joined
    /// up: what an operator creates is what the scanner is handed.
    /// </summary>
    [Fact]
    public async Task DiscoveryConfig_ReturnsTheEnabledProfilesOfItsOwnGroup()
    {
        var group = NewGroup();
        var other = NewGroup();
        var mine = await CreateAsync(NewName(), group, ["10.31.0.0/29"], [22]);
        await CreateAsync(NewName(), other, ["10.32.0.0/29"], []);
        await CreateAsync(NewName(), group, ["10.33.0.0/29"], [], isEnabled: false);

        var config = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole);

        Assert.Equal(group, config.DiscoveryGroup);
        var only = Assert.Single(config.Profiles);
        Assert.Equal(mine.Id, only.ScanProfileId);
        Assert.Equal(["10.31.0.0/29"], only.Ranges);
        Assert.Equal([22], only.Ports);
        // Seconds on the wire, minutes in the model: the scanner schedules against a monotonic clock
        // in seconds, so the conversion happens once, here.
        Assert.Equal(60 * 60, only.IntervalSeconds);
    }

    /// <summary>
    /// A group nobody has written a profile for is an empty list, not a 404. A scanner is deployed
    /// before anybody configures it, and answering 404 would make "nothing to scan yet" and "this
    /// platform has never heard of you" the same message on its first cycle.
    /// </summary>
    [Fact]
    public async Task DiscoveryConfig_ForAGroupWithNoProfiles_IsEmptyRatherThanNotFound()
    {
        var config = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{NewGroup()}/scan-profiles", DiscoveryRole);

        Assert.Empty(config.Profiles);
    }

    [Fact]
    public async Task ScanProfile_Disabled_LeavesTheScannersConfiguration()
    {
        var group = NewGroup();
        var created = await CreateAsync(NewName(), group, ["10.34.0.0/29"], []);
        Assert.Single((await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole)).Profiles);

        using var edit = Authenticated(HttpMethod.Put, $"/api/scan-profiles/{created.Id}");
        edit.Content = JsonContent.Create(new
        {
            name = created.Name,
            ranges = new[] { "10.34.0.0/29" },
            discoveryGroup = group,
            intervalMinutes = 60,
            timeoutSeconds = 2,
            isEnabled = false,
        });
        using var edited = await _client!.SendAsync(edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole);
        Assert.Empty(after.Profiles);
    }

    /// <summary>
    /// An update is a complete statement, like every other one in this module: an omitted port list
    /// clears the fingerprint step rather than leaving the previous one quietly in place.
    /// </summary>
    [Fact]
    public async Task ScanProfile_UpdatedWithoutPorts_ClearsThemRatherThanKeepingTheOldOnes()
    {
        var group = NewGroup();
        var created = await CreateAsync(NewName(), group, ["10.35.0.0/29"], [22, 443]);

        using var edit = Authenticated(HttpMethod.Put, $"/api/scan-profiles/{created.Id}");
        edit.Content = JsonContent.Create(new
        {
            name = created.Name,
            ranges = new[] { "10.35.0.0/29" },
            discoveryGroup = group,
            intervalMinutes = 60,
            timeoutSeconds = 2,
            isEnabled = true,
        });
        using var edited = await _client!.SendAsync(edit);
        var updated = Assert.IsType<ScanProfileDto>(
            await edited.Content.ReadFromJsonAsync<ScanProfileDto>());

        Assert.Empty(updated.Ports);
    }

    [Fact]
    public async Task ScanProfile_TheLocalKeyword_IsStoredAndHasNoAddressCount()
    {
        var created = await CreateAsync(NewName(), NewGroup(), ["local"], []);

        Assert.Equal(["local"], created.Ranges);
        // Its size depends on the interface the scanner finds, so a number here would be a guess
        // presented as a fact.
        Assert.Null(created.AddressCount);
    }

    [Fact]
    public async Task ScanProfile_Deleted_IsGoneFromTheListAndTheScannersConfiguration()
    {
        var group = NewGroup();
        var created = await CreateAsync(NewName(), group, ["10.36.0.0/29"], []);

        using var delete = Authenticated(HttpMethod.Delete, $"/api/scan-profiles/{created.Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var refetch = Authenticated(HttpMethod.Get, $"/api/scan-profiles/{created.Id}");
        using var response = await _client.SendAsync(refetch);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole)).Profiles);
    }

    /// <summary>Every write endpoint produces an audit entry — ARCHITECTURE §7.1.</summary>
    [Fact]
    public async Task ScanProfile_EveryWrite_IsAudited()
    {
        var created = await CreateAsync(NewName(), NewGroup(), ["10.37.0.0/29"], []);

        using var delete = Authenticated(HttpMethod.Delete, $"/api/scan-profiles/{created.Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var actions = await dbContext.AuditEntries.AsNoTracking()
            .Where(entry => entry.EntityType == "ScanProfile" && entry.EntityId == created.Id.ToString())
            .Select(entry => entry.Action)
            .ToListAsync();

        Assert.Equal(["Created", "Deleted"], actions.Order());
    }

    [Fact]
    public async Task ScanProfile_ARangeThatCannotBeScanned_IsRefusedWithAFieldError()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/scan-profiles");
        request.Content = JsonContent.Create(new
        {
            name = NewName(),
            ranges = new[] { "10.0.0.0/8" },
            discoveryGroup = NewGroup(),
            intervalMinutes = 60,
            timeoutSeconds = 2,
        });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("Ranges", problem, StringComparison.Ordinal);
        // Both numbers, so an operator can see what to type instead.
        Assert.Contains("65,536", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanProfile_WithNoRanges_IsRefused()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/scan-profiles");
        request.Content = JsonContent.Create(new
        {
            name = NewName(),
            ranges = Array.Empty<string>(),
            discoveryGroup = NewGroup(),
            intervalMinutes = 60,
            timeoutSeconds = 2,
        });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ScanProfile_ANameAlreadyUsed_IsAConflict()
    {
        var name = NewName();
        await CreateAsync(name, NewGroup(), ["10.38.0.0/29"], []);

        using var request = Authenticated(HttpMethod.Post, "/api/scan-profiles");
        request.Content = JsonContent.Create(new
        {
            name,
            ranges = new[] { "10.39.0.0/29" },
            discoveryGroup = NewGroup(),
            intervalMinutes = 60,
            timeoutSeconds = 2,
        });

        using var response = await _client!.SendAsync(request);

        // Two profiles with one name are indistinguishable in the log line that says which scan found
        // a device, which is the only place most people will ever read a profile's name.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// The failure path that matters most: <c>CanDiscover</c> is disjoint from every operator policy
    /// <em>and</em> from <c>CanPoll</c>. A scanner has no devices to configure and no credential scope
    /// to redeem, so the two service identities must not be interchangeable.
    /// </summary>
    [Theory]
    [InlineData("Technician")]
    [InlineData("Admin")]
    [InlineData(PollerRole)]
    public async Task DiscoveryConfig_WithAnyRoleButDiscovery_IsForbidden(string role)
    {
        using var request = Authenticated(
            HttpMethod.Get, $"/api/discovery/{NewGroup()}/scan-profiles", role);

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>And the reverse: a scanner cannot write itself a wider range to scan.</summary>
    [Theory]
    [InlineData(DiscoveryRole)]
    [InlineData("EndUser")]
    public async Task ScanProfiles_ManagedWithoutAnOperatorRole_IsForbidden(string role)
    {
        using var list = Authenticated(HttpMethod.Get, "/api/scan-profiles", role);
        using var listed = await _client!.SendAsync(list);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);

        using var create = Authenticated(HttpMethod.Post, "/api/scan-profiles", role);
        create.Content = JsonContent.Create(new
        {
            name = NewName(),
            ranges = new[] { "10.40.0.0/29" },
            intervalMinutes = 60,
            timeoutSeconds = 2,
        });
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task ScanProfile_ThatDoesNotExist_IsNotFound()
    {
        using var request = Authenticated(
            HttpMethod.Get, $"/api/scan-profiles/{Guid.CreateVersion7()}");

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Phase 5.5: the schedule switches and on-demand runs ----

    [Fact]
    public async Task ScanProfile_WithItsScheduleOff_StillReachesTheScannerAndSaysSo()
    {
        var group = NewGroup();

        var created = await CreateAsync(
            NewName(), group, ["10.31.0.0/29"], [], scheduleEnabled: false);

        Assert.False(created.ScheduleEnabled);
        Assert.True(created.IsEnabled);

        // The distinction the whole feature rests on: not scheduled, but still configured — so a
        // requested run can name it. Filtering it out here would make "scan now" work only for the
        // profiles that did not need it.
        var config = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole);
        var profile = Assert.Single(config.Profiles);
        Assert.Equal(created.Id, profile.ScanProfileId);
        Assert.False(profile.ScheduleEnabled);
    }

    [Fact]
    public async Task DiscoverySettings_ScheduledScanningSwitchedOff_IsCarriedOnEveryScannerFetch()
    {
        var group = NewGroup();
        await CreateAsync(NewName(), group, ["10.32.0.0/29"], []);

        var before = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole);
        Assert.True(before.ScheduledScanningEnabled);

        var settings = await SetScheduledScanningAsync(false);
        Assert.False(settings.ScheduledScanningEnabled);

        var after = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole);
        Assert.False(after.ScheduledScanningEnabled);

        // Still sent, still complete: the switch stops the clock rather than the scanner.
        Assert.Single(after.Profiles);

        await SetScheduledScanningAsync(true);
    }

    [Fact]
    public async Task ScanRun_RequestedThenClaimedThenReported_IsRecordedWithWhatTheScanFound()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.33.0.0/29"], [22]);

        var queued = await RequestRunAsync(profile.Id);
        Assert.Equal("Queued", queued.Status);
        Assert.Null(queued.DiscoveryName);
        Assert.Equal(profile.Name, queued.ScanProfileName);

        // The scanner's fetch, under its own policy. It is handed the whole profile rather than an
        // id, so it can run one that is not in its scheduled configuration.
        var dispatch = await GetAsync<ScanDispatchDto>(
            $"/api/discovery/{group}/scan-runs?discoveryName=discovery-1", DiscoveryRole);
        var claimed = Assert.Single(dispatch.Runs);
        Assert.Equal(queued.Id, claimed.ScanRunId);
        Assert.Equal(profile.Id, claimed.Profile.ScanProfileId);
        Assert.Equal(["10.33.0.0/29"], claimed.Profile.Ranges);

        var running = await GetAsync<ScanRunDto>($"/api/scan-runs/{queued.Id}");
        Assert.Equal("Running", running.Status);
        Assert.Equal("discovery-1", running.DiscoveryName);

        using var report = Authenticated(
            HttpMethod.Post, $"/api/discovery/{group}/scan-runs/{queued.Id}/results", DiscoveryRole);
        report.Content = JsonContent.Create(new
        {
            outcome = "Succeeded",
            addressesProbed = 6,
            devicesFound = 2,
        });
        using var reported = await _client!.SendAsync(report);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);

        var finished = await GetAsync<ScanRunDto>($"/api/scan-runs/{queued.Id}");
        Assert.Equal("Succeeded", finished.Status);
        Assert.Equal(6, finished.AddressesProbed);
        Assert.Equal(2, finished.DevicesFound);
        Assert.NotNull(finished.CompletedAt);
    }

    [Fact]
    public async Task ScanRun_ClaimedOnce_IsNotHandedToTheNextScannerThatAsks()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.34.0.0/29"], []);
        await RequestRunAsync(profile.Id);

        var first = await GetAsync<ScanDispatchDto>(
            $"/api/discovery/{group}/scan-runs?discoveryName=discovery-1", DiscoveryRole);
        var second = await GetAsync<ScanDispatchDto>(
            $"/api/discovery/{group}/scan-runs?discoveryName=discovery-2", DiscoveryRole);

        // Two scanners may share a group; the claim is a conditional update, so only one wins.
        Assert.Single(first.Runs);
        Assert.Empty(second.Runs);
    }

    [Fact]
    public async Task ScanRun_RequestedTwiceWhileOneIsWaiting_IsRefusedWithTheRunAlreadyQueued()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.35.0.0/29"], []);
        var first = await RequestRunAsync(profile.Id);

        using var again = Authenticated(HttpMethod.Post, $"/api/scan-profiles/{profile.Id}/runs");
        again.Content = JsonContent.Create(new { note = (string?)null });
        using var response = await _client!.SendAsync(again);

        // A second press is a 409 naming the scan that is already coming, rather than a second row.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains(profile.Name, problem, StringComparison.Ordinal);

        // And exactly one run exists, which is the constraint doing the work rather than the check.
        var runs = await GetAsync<ScanRunPageDto>($"/api/scan-runs?scanProfileId={profile.Id}");
        Assert.Equal(1, runs.Total);
        Assert.Equal(first.Id, runs.Items[0].Id);
    }

    [Fact]
    public async Task ScanRun_OfADisabledProfile_IsRefusedBeforeItCanSitQueuedForever()
    {
        var group = NewGroup();
        var profile = await CreateAsync(
            NewName(), group, ["10.36.0.0/29"], [], isEnabled: false);

        using var request = Authenticated(HttpMethod.Post, $"/api/scan-profiles/{profile.Id}/runs");
        request.Content = JsonContent.Create(new { note = "why not" });
        using var response = await _client!.SendAsync(request);

        // A disabled profile has left every scanner's configuration, so a run of it would sit
        // queued until it timed out. Saying so while somebody is looking at the button is kinder.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ScanRun_ReportedWithAnOutcomeAScannerMayNotDeclare_IsRejected()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.37.0.0/29"], []);
        var queued = await RequestRunAsync(profile.Id);
        await GetAsync<ScanDispatchDto>(
            $"/api/discovery/{group}/scan-runs?discoveryName=discovery-1", DiscoveryRole);

        using var report = Authenticated(
            HttpMethod.Post, $"/api/discovery/{group}/scan-runs/{queued.Id}/results", DiscoveryRole);
        // TimedOut is the platform's verdict about this scanner. Letting an agent report it would
        // let a scanner that gave up describe itself as having been abandoned.
        report.Content = JsonContent.Create(new { outcome = "TimedOut" });
        using var response = await _client!.SendAsync(report);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ScanRun_ReportedTwice_KeepsTheFirstTerminalStateAndAnswers409()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.38.0.0/29"], []);
        var queued = await RequestRunAsync(profile.Id);
        await GetAsync<ScanDispatchDto>(
            $"/api/discovery/{group}/scan-runs?discoveryName=discovery-1", DiscoveryRole);

        await ReportAsync(group, queued.Id, "Succeeded", HttpStatusCode.OK);
        await ReportAsync(group, queued.Id, "Failed", HttpStatusCode.Conflict);

        var finished = await GetAsync<ScanRunDto>($"/api/scan-runs/{queued.Id}");
        Assert.Equal("Succeeded", finished.Status);
    }

    /// <summary>
    /// The two halves are disjoint in both directions: an operator cannot collect a run, and a
    /// scanner cannot ask for one. It is WP-5.6's rule for the runbook channel, in the second place
    /// this solution has an agent channel.
    /// </summary>
    [Fact]
    public async Task ScanRuns_TheOperatorAndScannerChannelsAreDisjointInBothDirections()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.39.0.0/29"], []);

        using var operatorClaim = Authenticated(
            HttpMethod.Get, $"/api/discovery/{group}/scan-runs", "Technician");
        using var claimed = await _client!.SendAsync(operatorClaim);
        Assert.Equal(HttpStatusCode.Forbidden, claimed.StatusCode);

        using var scannerRequest = Authenticated(
            HttpMethod.Post, $"/api/scan-profiles/{profile.Id}/runs", DiscoveryRole);
        scannerRequest.Content = JsonContent.Create(new { note = (string?)null });
        using var requested = await _client.SendAsync(scannerRequest);
        Assert.Equal(HttpStatusCode.Forbidden, requested.StatusCode);

        // And the poller, which is a different agent again: it has no business here at all.
        using var pollerClaim = Authenticated(
            HttpMethod.Get, $"/api/discovery/{group}/scan-runs", PollerRole);
        using var polled = await _client.SendAsync(pollerClaim);
        Assert.Equal(HttpStatusCode.Forbidden, polled.StatusCode);
    }

    [Fact]
    public async Task ScanRun_ClaimedByAScannerThatNeverReports_IsTimedOutBySweeperNotLeftRunning()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.41.0.0/29"], []);
        var queued = await RequestRunAsync(profile.Id);
        await GetAsync<ScanDispatchDto>(
            $"/api/discovery/{group}/scan-runs?discoveryName=discovery-1", DiscoveryRole);

        // Reach past the deadline rather than wait for it. This is the only thing that notices a
        // dead scanner: WP-4.1 gave this service no heartbeat, so silence is the sole symptom.
        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            var run = await dbContext.ScanRuns.SingleAsync(item => item.Id == queued.Id);
            run.DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await dbContext.SaveChangesAsync();

            var swept = await scope.ServiceProvider
                .GetRequiredService<IScanRunTimeoutSweeper>().SweepAsync(default);
            Assert.True(swept >= 1);
        }

        var finished = await GetAsync<ScanRunDto>($"/api/scan-runs/{queued.Id}");
        Assert.Equal("TimedOut", finished.Status);
        Assert.NotNull(finished.Error);
    }

    [Fact]
    public async Task ScanRun_ReportingProgress_MovesTheCountsWithoutFinishingTheRun()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.42.0.0/24"], []);
        var queued = await RequestRunAsync(profile.Id);
        await GetAsync<ScanDispatchDto>(
            $"/api/discovery/{group}/scan-runs?discoveryName=discovery-1", DiscoveryRole);

        using var progress = Authenticated(
            HttpMethod.Post, $"/api/discovery/{group}/scan-runs/{queued.Id}/progress", DiscoveryRole);
        progress.Content = JsonContent.Create(new
        {
            addressesProbed = 128,
            addressesTotal = 254,
            lastRespondingAddress = "172.18.0.7",
        });
        using var reported = await _client!.SendAsync(progress);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);

        var running = await GetAsync<ScanRunDto>($"/api/scan-runs/{queued.Id}");
        Assert.Equal(128, running.AddressesProbed);
        Assert.Equal(254, running.AddressesTotal);
        Assert.Equal("172.18.0.7", running.LastRespondingAddress);
        Assert.NotNull(running.ProgressAt);

        // The point of a separate endpoint: progress says how far, never whether it is done.
        Assert.Equal("Running", running.Status);
        Assert.Null(running.CompletedAt);
    }

    [Fact]
    public async Task ScanRun_ProgressReportedAfterItFinished_IsRefusedRatherThanDraggingItBackwards()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.43.0.0/29"], []);
        var queued = await RequestRunAsync(profile.Id);
        await GetAsync<ScanDispatchDto>(
            $"/api/discovery/{group}/scan-runs?discoveryName=discovery-1", DiscoveryRole);
        await ReportAsync(group, queued.Id, "Succeeded", HttpStatusCode.OK);

        using var late = Authenticated(
            HttpMethod.Post, $"/api/discovery/{group}/scan-runs/{queued.Id}/progress", DiscoveryRole);
        late.Content = JsonContent.Create(new { addressesProbed = 3, addressesTotal = 6 });
        using var response = await _client!.SendAsync(late);

        // A slow progress post arriving after the result must not un-finish the row.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var finished = await GetAsync<ScanRunDto>($"/api/scan-runs/{queued.Id}");
        Assert.Equal("Succeeded", finished.Status);
        Assert.Equal(6, finished.AddressesProbed);
    }

    [Fact]
    public async Task ScanRun_ProgressFromAnOperator_IsForbidden()
    {
        var group = NewGroup();
        var profile = await CreateAsync(NewName(), group, ["10.44.0.0/29"], []);
        var queued = await RequestRunAsync(profile.Id);

        using var request = Authenticated(
            HttpMethod.Post, $"/api/discovery/{group}/scan-runs/{queued.Id}/progress", "Technician");
        request.Content = JsonContent.Create(new { addressesProbed = 1 });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<ScanRunDto> RequestRunAsync(Guid scanProfileId)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/scan-profiles/{scanProfileId}/runs");
        request.Content = JsonContent.Create(new { note = "verification" });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return Assert.IsType<ScanRunDto>(await response.Content.ReadFromJsonAsync<ScanRunDto>());
    }

    private async Task ReportAsync(
        string group, Guid runId, string outcome, HttpStatusCode expected)
    {
        using var request = Authenticated(
            HttpMethod.Post, $"/api/discovery/{group}/scan-runs/{runId}/results", DiscoveryRole);
        request.Content = JsonContent.Create(new { outcome, addressesProbed = 6, devicesFound = 0 });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    private async Task<DiscoverySettingsDto> SetScheduledScanningAsync(bool enabled)
    {
        using var request = Authenticated(HttpMethod.Put, "/api/discovery-settings");
        request.Content = JsonContent.Create(new { scheduledScanningEnabled = enabled });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<DiscoverySettingsDto>(
            await response.Content.ReadFromJsonAsync<DiscoverySettingsDto>());
    }

    private static string NewGroup() => $"group-{Guid.NewGuid():N}"[..20];

    private static string NewName() => $"Scan {Guid.NewGuid():N}"[..20];

    private async Task<ScanProfileDto> CreateAsync(
        string name,
        string discoveryGroup,
        string[] ranges,
        int[] ports,
        bool isEnabled = true,
        bool scheduleEnabled = true)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/scan-profiles");
        request.Content = JsonContent.Create(new
        {
            name,
            ranges,
            discoveryGroup,
            ports,
            intervalMinutes = 60,
            timeoutSeconds = 2,
            isEnabled,
            scheduleEnabled,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<ScanProfileDto>(await response.Content.ReadFromJsonAsync<ScanProfileDto>());
    }

    private async Task<T> GetAsync<T>(string uri, string role = "Technician")
    {
        using var request = Authenticated(HttpMethod.Get, uri, role);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static HttpRequestMessage Authenticated(
        HttpMethod method,
        string uri,
        string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(ScanProfileAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record ScanProfileDto(
        Guid Id,
        string Name,
        string? Description,
        string DiscoveryGroup,
        IReadOnlyList<string> Ranges,
        IReadOnlyList<int> Ports,
        int IntervalMinutes,
        int TimeoutSeconds,
        bool SnmpEnabled,
        bool NeighbourDiscoveryEnabled,
        bool IsEnabled,
        bool ScheduleEnabled,
        long? AddressCount);

    private sealed record DiscoveryConfigDto(
        string DiscoveryGroup,
        IReadOnlyList<DiscoveryScanProfileDto> Profiles,
        DateTimeOffset GeneratedAt,
        bool ScheduledScanningEnabled);

    private sealed record DiscoveryScanProfileDto(
        Guid ScanProfileId,
        string Name,
        IReadOnlyList<string> Ranges,
        IReadOnlyList<int> Ports,
        int IntervalSeconds,
        int TimeoutSeconds,
        bool SnmpEnabled,
        bool NeighbourDiscoveryEnabled,
        bool ScheduleEnabled);

    private sealed record ScanRunDto(
        Guid Id,
        Guid ScanProfileId,
        string ScanProfileName,
        string DiscoveryGroup,
        string Status,
        string RequestedBy,
        DateTimeOffset RequestedAt,
        string? DiscoveryName,
        DateTimeOffset? DispatchedAt,
        DateTimeOffset? DeadlineAt,
        DateTimeOffset? CompletedAt,
        int? AddressesProbed,
        int? AddressesTotal,
        int? DevicesFound,
        string? LastRespondingAddress,
        DateTimeOffset? ProgressAt,
        string? Error);

    private sealed record ScanRunPageDto(
        IReadOnlyList<ScanRunDto> Items,
        int Total,
        int Page,
        int PageSize);

    private sealed record ScanDispatchDto(
        string DiscoveryGroup,
        IReadOnlyList<ScanDispatchItemDto> Runs,
        DateTimeOffset GeneratedAt);

    private sealed record ScanDispatchItemDto(
        Guid ScanRunId,
        DateTimeOffset DeadlineAt,
        DiscoveryScanProfileDto Profile);

    private sealed record DiscoverySettingsDto(
        bool ScheduledScanningEnabled,
        string UpdatedBy,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// Its own host rather than a shared one, following every other API test class here. A scan profile
    /// needs no CI, so this host does not migrate the assets schema — but it does migrate Platform's,
    /// because every write is audited.
    /// </summary>
    private sealed class ScanProfileApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public ScanProfileApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
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
                    ["ConnectionStrings:minio"] = _minioConnectionString,
                    ["ObjectStorage:AccessKey"] = "minioadmin",
                    ["ObjectStorage:SecretKey"] = "minio-test-password",
                    ["Platform:ApplyMigrations"] = "false",
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ScanProfileAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = ScanProfileAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = ScanProfileAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ScanProfileAuthenticationHandler>(
                        ScanProfileAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", null);
        }
    }

    private sealed class ScanProfileAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "ScanProfileTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "discovery-test-user-id"),
                    new Claim(ClaimTypes.Name, "discovery-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
