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

using Modules.Assets.Data;
using Modules.Helpdesk.Data;
using Modules.Monitoring.Data;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The CI timeline end to end: an asset that was registered, moved into service, checked out, edited,
/// alerted twice and had a ticket raised about it — all of it arriving on one axis from one call to
/// <c>GET /api/cis/{id}/timeline</c>.
/// <para>
/// The interleaving and the wording are asserted in <see cref="CiTimelineAssemblerTests"/> against
/// hand-written history. What this class exists to prove is the plumbing that test cannot see: the two
/// cross-module reads through their ports, the audit trail read through Platform, the filter reaching the
/// queries rather than the rendering, and the JSON that arrives in the browser.
/// </para>
/// <para>
/// Like the blast radius and unlike the drift and audit suites, this needs no site of its own: every read
/// is rooted at one CI this class created, so it cannot compete with what other classes have written to
/// the shared database.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CiTimelineApiIntegrationTests : IAsyncLifetime
{
    private readonly TimelineApplication _application;
    private HttpClient? _client;

    public CiTimelineApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        _application = new TimelineApplication(
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

        // The port trap, for the ninth time — and this package brings two more ports with it, so both
        // halves of it are load-bearing here rather than incidental: the timeline reads Helpdesk and
        // Monitoring by design, and an unmigrated schema behind either answers 42P01 as a 500 from a query
        // that mentions neither Assets nor this feature.
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _application.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The WP's own verification step: a device with a history shows correctly ordered mixed events. All
    /// four sources are on the axis, and the axis runs newest to oldest without exception.
    /// </summary>
    [Fact]
    public async Task Timeline_ForACiWithHistoryFromEverySource_ReturnsThemAllInOrder()
    {
        var estate = await BuildHistoryAsync();

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{estate.CiId}/timeline");

        Assert.Equal(estate.CiId, timeline.CiId);
        Assert.Contains(timeline.Entries, entry => entry.Kind == "Alert");
        Assert.Contains(timeline.Entries, entry => entry.Kind == "Ticket");
        Assert.Contains(timeline.Entries, entry => entry.Kind == "Lifecycle");
        Assert.Contains(timeline.Entries, entry => entry.Kind == "Config");

        Assert.Equal(
            timeline.Entries.Select(entry => entry.OccurredAt).OrderByDescending(at => at),
            timeline.Entries.Select(entry => entry.OccurredAt));

        // The five events this class placed in the past, in the order they must come back. Asserted on
        // their own rather than over the whole axis because the edits ride at whatever moment the suite
        // ran, and what is under test is the interleaving rather than the clock.
        var placed = timeline.Entries
            .Select(entry => entry.Id)
            .Where(id => estate.Placed.Contains(id))
            .ToList();
        Assert.Equal(
            [estate.OpenAlertId, estate.TicketId, estate.TransitionId, estate.AssignmentId, estate.ClearedAlertId],
            placed);
    }

    /// <summary>
    /// The other half of the WP's verification: filtering to alerts works. The other three sources come
    /// back marked as never asked, which is a different statement from "there are none".
    /// </summary>
    [Fact]
    public async Task Timeline_FilteredToAlerts_ReturnsOnlyAlertsAndSaysTheOtherSourcesWereNotAsked()
    {
        var estate = await BuildHistoryAsync();

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{estate.CiId}/timeline?types=alert");

        Assert.Equal(2, timeline.Entries.Count);
        Assert.All(timeline.Entries, entry => Assert.Equal("Alert", entry.Kind));
        Assert.Equal(["Alert"], timeline.Kinds);

        var tickets = Assert.Single(timeline.Sources, source => source.Kind == "Ticket");
        Assert.False(tickets.Requested);
        Assert.Equal(0, tickets.Total);

        var alerts = Assert.Single(timeline.Sources, source => source.Kind == "Alert");
        Assert.True(alerts.Requested);
        Assert.Equal(2, alerts.Total);
    }

    /// <summary>
    /// Two kinds at once, which is the filter an operator actually reaches for: what broke and what was
    /// done about it, without the record edits in between.
    /// </summary>
    [Fact]
    public async Task Timeline_FilteredToTwoKinds_ReturnsBothAndNothingElse()
    {
        var estate = await BuildHistoryAsync();

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{estate.CiId}/timeline?types=alert,ticket");

        Assert.Equal(["Alert", "Ticket"], timeline.Kinds);
        Assert.All(timeline.Entries, entry => Assert.Contains(entry.Kind, new[] { "Alert", "Ticket" }));
        Assert.Contains(timeline.Entries, entry => entry.TicketId == estate.TicketId);
    }

    /// <summary>
    /// The alert half of the read, through Monitoring's port: the recovery is stated on the row the alert
    /// was raised on rather than as a second event, and a suppressed alert is on the axis with its reason.
    /// </summary>
    [Fact]
    public async Task Timeline_ForAnAlertThatRecoveredAndOneSuppressedUnderARootCause_SaysSoOnBothRows()
    {
        var estate = await BuildHistoryAsync();

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{estate.CiId}/timeline?types=alert");

        var cleared = Assert.Single(timeline.Entries, entry => entry.Id == estate.ClearedAlertId);
        Assert.Equal("Cleared", cleared.Status);
        Assert.Contains("recovered after 25 minutes", cleared.Detail!, StringComparison.Ordinal);

        var open = Assert.Single(timeline.Entries, entry => entry.Id == estate.OpenAlertId);
        Assert.Equal("Critical", open.Severity);
        Assert.Contains("suppressed under its root cause", open.Detail!, StringComparison.Ordinal);
        Assert.Equal(estate.DeviceId, open.DeviceId);
    }

    /// <summary>
    /// The ticket half, through Helpdesk's port: the entry sits at the moment the ticket was raised and
    /// carries the later moment somebody attached it to this asset.
    /// </summary>
    [Fact]
    public async Task Timeline_ForATicketLinkedAfterItWasRaised_SitsAtTheRaisedMomentAndCarriesTheLink()
    {
        var estate = await BuildHistoryAsync();

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{estate.CiId}/timeline?types=ticket");

        var ticket = Assert.Single(timeline.Entries);
        Assert.Equal(estate.TicketId, ticket.TicketId);
        Assert.StartsWith("INC-", ticket.TicketNumber!, StringComparison.Ordinal);
        Assert.Equal(estate.TicketRaisedAt, ticket.OccurredAt);
        Assert.Equal(estate.TicketLinkedAt, ticket.LinkedAt);
    }

    /// <summary>
    /// A lifecycle move writes both a lifecycle row and an audit row. Only the first is worth reading, so
    /// the config source leaves the second out — otherwise the axis carries "In stock → Deployed" and, on
    /// the same second, "Record updated. Changed lifecycleState, updatedAt."
    /// </summary>
    [Fact]
    public async Task Timeline_ForALifecycleMove_ShowsItOnceAndNotAgainAsAnAudit()
    {
        var estate = await BuildHistoryAsync();

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{estate.CiId}/timeline");

        Assert.Single(timeline.Entries, entry => entry.Title == "In stock → Deployed");
        Assert.DoesNotContain(
            timeline.Entries.Where(entry => entry.Kind == "Config"),
            entry => entry.Detail?.Contains("lifecycleState", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// The audited edit, through Platform: an operator reading "Record updated" six times learns nothing,
    /// so the entry names the fields that actually moved.
    /// </summary>
    [Fact]
    public async Task Timeline_ForAnEditedRecord_NamesTheFieldThatChanged()
    {
        var estate = await BuildHistoryAsync();

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{estate.CiId}/timeline?types=config");

        Assert.Contains(timeline.Entries, entry => entry.Title == "Registered in the CMDB");
        var edit = Assert.Single(timeline.Entries, entry => entry.Title == "Record updated");
        Assert.Contains("name", edit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window narrows the read at every source rather than after the merge, so a timeline asked about
    /// yesterday costs yesterday.
    /// </summary>
    [Fact]
    public async Task Timeline_WithAWindow_ReturnsOnlyWhatHappenedInsideIt()
    {
        var estate = await BuildHistoryAsync();
        var from = Uri.EscapeDataString(estate.TicketRaisedAt.AddHours(-1).ToString("O"));

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{estate.CiId}/timeline?from={from}");

        Assert.Contains(timeline.Entries, entry => entry.Id == estate.TicketId);
        // Three days old, and therefore outside the window — including in the source's own total, which
        // is what makes the count on screen match the window rather than the estate's whole history.
        Assert.DoesNotContain(timeline.Entries, entry => entry.Id == estate.ClearedAlertId);
        Assert.Equal(1, Assert.Single(timeline.Sources, source => source.Kind == "Alert").Total);
    }

    /// <summary>
    /// A CI registered a moment ago is not a CI nothing has ever happened to: it was registered, and that
    /// is the first thing on its axis. An empty timeline here would read as a broken feature.
    /// </summary>
    [Fact]
    public async Task Timeline_ForAFreshlyCreatedCi_ShowsItsRegistrationAndNothingElse()
    {
        var ciId = await CreateCiAsync("Standalone jump box");

        var timeline = await GetAsync<TimelineDto>($"/api/cis/{ciId}/timeline");

        var entry = Assert.Single(timeline.Entries);
        Assert.Equal("Config", entry.Kind);
        Assert.Equal("Registered in the CMDB", entry.Title);
        Assert.Equal(1, timeline.Summary.TotalCount);
        Assert.False(timeline.Summary.Truncated);
    }

    /// <summary>
    /// The failure path that matters most for a filter: a kind the server does not recognise is refused,
    /// naming every kind, rather than silently ignored. A dropped filter answers a different question from
    /// the one that was asked and looks exactly like a filter that does not work.
    /// </summary>
    [Fact]
    public async Task Timeline_WithAnUnrecognisedTypeFilter_Is400NamingTheKindsItAccepts()
    {
        var ciId = await CreateCiAsync("Filter refusal");

        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{ciId}/timeline?types=alerts");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("types", problem, StringComparison.Ordinal);
        Assert.Contains("Lifecycle", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same refusal for a number, which is the shape that gets through by accident: <c>Enum.TryParse</c>
    /// accepts any integer, so without the definedness check this would parse to a kind that does not
    /// exist, match no source, and answer with an empty timeline and no complaint at all.
    /// </summary>
    [Fact]
    public async Task Timeline_WithATypeFilterThatIsAnUndefinedNumber_Is400RatherThanAnEmptyTimeline()
    {
        var ciId = await CreateCiAsync("Numeric filter refusal");

        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{ciId}/timeline?types=99");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A window that ends before it starts is a typo, not an asset with no history.</summary>
    [Fact]
    public async Task Timeline_WithAWindowThatEndsBeforeItStarts_Is400()
    {
        var ciId = await CreateCiAsync("Backwards window");
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));

        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{ciId}/timeline?from={from}&to={to}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A CI id nothing answers to is a 404 about the CI, not a timeline with no events on it.</summary>
    [Fact]
    public async Task Timeline_ForACiThatDoesNotExist_Is404()
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{Guid.CreateVersion7()}/timeline");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The CMDB is an agent surface (WP-2.1), and a timeline names other people's tickets, the people who
    /// raised them and everybody who has ever edited the record.
    /// </summary>
    [Fact]
    public async Task Timeline_AsEndUser_IsForbidden()
    {
        var ciId = await CreateCiAsync("Forbidden to end users");

        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{ciId}/timeline", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// One server's history, built the way it would really happen: registered and moved into service
    /// through their own endpoints, then the parts that need a past back-dated in place. The alerts and
    /// the assignment are written directly — an alert is raised by a poller and an assignment endpoint
    /// needs directory rows this shared database may not hold — and what is under test is the read.
    /// </summary>
    private async Task<History> BuildHistoryAsync()
    {
        var ciId = await CreateCiAsync("DC1 hypervisor host");
        await TransitionAsync(ciId, "Deployed", "Racked in DC1.");
        await RenameAsync(ciId, Shorten($"dc1-esx-{Guid.NewGuid():N}", 24));

        var ticketId = await CreateTicketAsync("ERP is unreachable");
        await LinkAsync(ticketId, ciId);

        var now = DateTimeOffset.UtcNow;
        var ticketRaisedAt = Truncate(now.AddDays(-1));
        var ticketLinkedAt = ticketRaisedAt.AddHours(6);
        var transitionAt = Truncate(now.AddDays(-2));
        var assignmentAt = Truncate(now.AddDays(-2).AddHours(-6));
        var clearedRaisedAt = Truncate(now.AddDays(-3));
        var openRaisedAt = Truncate(now.AddHours(-2));

        Guid assignmentId;
        Guid transitionId;
        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();

            // The transition really happened through its endpoint a moment ago; back-dated here so the
            // axis has an order worth asserting rather than five events on the same second.
            var transition = await assets.CiLifecycleHistory.FirstAsync(entry => entry.CiId == ciId);
            transition.OccurredAt = transitionAt;
            transitionId = transition.Id;

            assignmentId = Guid.CreateVersion7();
            assets.CiAssignments.Add(new CiAssignmentEntry
            {
                Id = assignmentId,
                CiId = ciId,
                Action = CiAssignmentAction.CheckOut,
                ToOwnerName = "Alex Doe",
                DepartmentName = "Finance",
                SiteName = "Head Office",
                ActorId = "timeline-test-subject",
                OccurredAt = assignmentAt,
            });
            await assets.SaveChangesAsync();

            var helpdesk = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var ticket = await helpdesk.Tickets.FirstAsync(entity => entity.Id == ticketId);
            ticket.CreatedAt = ticketRaisedAt;
            var link = await helpdesk.TicketCiLinks
                .FirstAsync(entity => entity.TicketId == ticketId && entity.CiId == ciId);
            link.LinkedAt = ticketLinkedAt;
            await helpdesk.SaveChangesAsync();
        }

        var (deviceId, clearedAlertId, openAlertId) =
            await RaiseAlertsAsync(ciId, clearedRaisedAt, openRaisedAt);

        return new History(
            ciId,
            deviceId,
            ticketId,
            ticketRaisedAt,
            ticketLinkedAt,
            transitionId,
            assignmentId,
            clearedAlertId,
            openAlertId,
            [openAlertId, ticketId, transitionId, assignmentId, clearedAlertId]);
    }

    /// <summary>
    /// One monitored device on the CI with two alerts against it: one that recovered after 25 minutes, and
    /// one still open and suppressed under a root cause somewhere else in the estate.
    /// <para>
    /// Written through the DbContext rather than raised by the alert engine, which would need a poller, a
    /// telemetry batch and three cycles of sustain to produce two rows this read does not care how it got.
    /// </para>
    /// </summary>
    private async Task<(Guid DeviceId, Guid ClearedAlertId, Guid OpenAlertId)> RaiseAlertsAsync(
        Guid ciId,
        DateTimeOffset clearedRaisedAt,
        DateTimeOffset openRaisedAt)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var monitoring = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

        var deviceId = Guid.CreateVersion7();
        monitoring.MonitoredDevices.Add(new MonitoredDevice
        {
            Id = deviceId,
            CiId = ciId,
            Address = $"10.10.0.{Random.Shared.Next(2, 250)}",
            PollerGroup = "default",
            CreatedBy = "timeline-test-subject",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "timeline-test-subject",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var clearedAlertId = Guid.CreateVersion7();
        var openAlertId = Guid.CreateVersion7();
        monitoring.Alerts.AddRange(
            new Alert
            {
                Id = clearedAlertId,
                DeviceId = deviceId,
                CiId = ciId,
                CheckId = Guid.CreateVersion7(),
                RuleId = "check:availability",
                MetricName = "check.success",
                Severity = AlertSeverity.Warning,
                Status = AlertStatus.Cleared,
                Summary = "No response to ICMP",
                Suppression = AlertSuppression.None,
                RaisedAt = clearedRaisedAt,
                LastObservedAt = clearedRaisedAt.AddMinutes(25),
                ClearedAt = clearedRaisedAt.AddMinutes(25),
                PollerName = "timeline-test",
            },
            new Alert
            {
                Id = openAlertId,
                DeviceId = deviceId,
                CiId = ciId,
                CheckId = Guid.CreateVersion7(),
                RuleId = "check:cpu",
                MetricName = "cpu.percent",
                Severity = AlertSeverity.Critical,
                Status = AlertStatus.Open,
                Summary = "CPU above 90%",
                // WP-5.1: recorded, shown, and never published. The timeline is where somebody finds out
                // this machine was affected even though nobody was paged about it.
                Suppression = AlertSuppression.RootCause,
                RaisedAt = openRaisedAt,
                LastObservedAt = openRaisedAt,
                PollerName = "timeline-test",
            });

        await monitoring.SaveChangesAsync();
        return (deviceId, clearedAlertId, openAlertId);
    }

    /// <summary>
    /// Postgres keeps microseconds and .NET keeps ticks, so an instant written here and read back is not
    /// bit-for-bit the one that went in. Truncated on the way in so the assertions compare like with like.
    /// </summary>
    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);

    private async Task<Guid> CreateCiAsync(string name)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "Server",
            // Unique, because the suite shares its database and CI names are asserted on by other
            // classes; trimmed to the column's limit without assuming the caller's name is long enough
            // to reach it.
            name = Shorten($"{name} {Guid.NewGuid():N}", 48),
            lifecycleState = "InStock",
            attributes = new Dictionary<string, string>
            {
                ["hostname"] = Shorten($"host-{Guid.NewGuid():N}", 20),
                ["operatingSystem"] = "VMware ESXi 8.0",
                ["cpuCores"] = "32",
                ["ramGb"] = "512",
            },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>()).Id;
    }

    private static string Shorten(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private async Task TransitionAsync(Guid ciId, string targetState, string note)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/cis/{ciId}/lifecycle-transitions");
        request.Content = JsonContent.Create(new { targetState, note });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// An ordinary edit through the CI's own endpoint, which is what writes the audit row.
    /// <para>
    /// The attributes are read back and re-sent because a PUT is a whole statement of the CI: omitting
    /// them clears the ones its type requires and the edit is refused. Re-sending them unchanged is also
    /// what makes the assertion sharp — the audit diff has one field in it and it is the name.
    /// </para>
    /// </summary>
    private async Task RenameAsync(Guid ciId, string name)
    {
        var current = await GetAsync<CiDto>($"/api/cis/{ciId}");
        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ciId}");
        request.Content = JsonContent.Create(new { name, isActive = true, attributes = current.Attributes });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> CreateTicketAsync(string title)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/tickets");
        request.Content = JsonContent.Create(new
        {
            title,
            description = "Raised by the CI timeline integration test.",
            type = "Incident",
            urgency = "Medium",
            impact = "Medium",
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TicketDto>(await response.Content.ReadFromJsonAsync<TicketDto>()).Id;
    }

    private async Task LinkAsync(Guid ticketId, Guid ciId)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticketId}/cis");
        request.Content = JsonContent.Create(new { ciId });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
        request.Headers.Add(TimelineAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record History(
        Guid CiId,
        Guid DeviceId,
        Guid TicketId,
        DateTimeOffset TicketRaisedAt,
        DateTimeOffset TicketLinkedAt,
        Guid TransitionId,
        Guid AssignmentId,
        Guid ClearedAlertId,
        Guid OpenAlertId,
        IReadOnlyList<Guid> Placed);

    private sealed record CiDto(Guid Id, string Type, string Name, Dictionary<string, string> Attributes);

    private sealed record TicketDto(Guid Id, string Number, string Title);

    private sealed record TimelineEntryDto(
        string Kind, Guid Id, DateTimeOffset OccurredAt, string Title, string? Detail, string? Actor,
        string? Severity, string? Status, string? Priority, Guid? AlertId, Guid? DeviceId,
        Guid? TicketId, string? TicketNumber, DateTimeOffset? LinkedAt);

    private sealed record TimelineSourceDto(string Kind, bool Requested, int Returned, int Total, bool Truncated);

    private sealed record TimelineSummaryDto(
        int EntryCount, int TotalCount, bool Truncated, DateTimeOffset? EarliestAt, DateTimeOffset? LatestAt);

    private sealed record TimelineDto(
        Guid CiId, string CiName, DateTimeOffset? From, DateTimeOffset? To, int Limit,
        List<string> Kinds, TimelineSummaryDto Summary, List<TimelineSourceDto> Sources,
        List<TimelineEntryDto> Entries);

    private sealed class TimelineApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public TimelineApplication(
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
                    // Creating a CI, moving it and raising a ticket all publish through the outbox, so the
                    // bus has to be configured even though nothing here reads a message. Every hosted
                    // service is removed below, so no sweeper of this host's competes with another suite's.
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TimelineAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = TimelineAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = TimelineAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TimelineAuthenticationHandler>(
                        TimelineAuthenticationHandler.TestScheme,
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

    private sealed class TimelineAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "TimelineTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "timeline-test-subject"),
                    new Claim("sub", "timeline-test-subject"),
                    new Claim("name", "Timeline Test"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
