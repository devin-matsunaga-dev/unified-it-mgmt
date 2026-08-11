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
using Modules.Monitoring.Features.Alerting;
using Modules.Monitoring.Features.Dashboards;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// WP-3.9's read surface against a real database: the alert board, the status board and the one write
/// either of them has. Alert rows are written directly rather than driven through the engine — the
/// engine is WP-3.5's and has its own tests; what is under test here is what an operator can read and
/// what happens when they press Acknowledge.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class AlertBoardApiIntegrationTests : IAsyncLifetime
{
    private readonly AlertBoardApplication _application;
    private HttpClient? _client;

    public AlertBoardApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new AlertBoardApplication(
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
        // GET /api/alerts/{id} asks Helpdesk what is already being worked on for the CI, through the
        // WP-3.7 port. A host that reads another module through a port needs that module's schema —
        // the same trap the WP-3.6 tests hit, and the symptom is a 500 rather than a DI failure.
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
        Broadcasts.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The board's whole job in one assertion: worst first. Severity is stored as its own name, so an
    /// ordering that let Postgres sort the column would read Critical, Ok, Warning and bury the
    /// warnings under the recoveries.
    /// </summary>
    [Fact]
    public async Task Alerts_Listed_AreOrderedWorstFirstThenNewest()
    {
        var device = await CreateDeviceAsync();
        var older = DateTimeOffset.UtcNow.AddHours(-2);
        await WriteAlertsAsync(
            Alert(device, "warning-rule", AlertSeverity.Warning, "CPU is high", older),
            Alert(device, "critical-rule", AlertSeverity.Critical, "Host is unreachable", older.AddHours(1)),
            Alert(device, "second-warning", AlertSeverity.Warning, "Memory is high", older.AddMinutes(90)));

        var page = await GetAsync<AlertPageDto>($"/api/alerts?deviceId={device.Id}");

        Assert.Equal(
            ["Host is unreachable", "Memory is high", "CPU is high"],
            page.Items.Select(alert => alert.Summary));
        Assert.Equal(3, page.Total);
    }

    /// <summary>A board asked nothing shows what is wrong now; history has to be asked for by name.</summary>
    [Fact]
    public async Task Alerts_WithNoStatusNamed_ShowOnlyOpenOnes()
    {
        var device = await CreateDeviceAsync();
        await WriteAlertsAsync(
            Alert(device, "open-rule", AlertSeverity.Critical, "Still wrong", DateTimeOffset.UtcNow),
            Cleared(Alert(device, "cleared-rule", AlertSeverity.Ok, "Recovered", DateTimeOffset.UtcNow)));

        var open = await GetAsync<AlertPageDto>($"/api/alerts?deviceId={device.Id}");
        Assert.Equal(["Still wrong"], open.Items.Select(alert => alert.Summary));

        var cleared = await GetAsync<AlertPageDto>($"/api/alerts?deviceId={device.Id}&status=Cleared");
        Assert.Equal(["Recovered"], cleared.Items.Select(alert => alert.Summary));
    }

    /// <summary>
    /// WP-3.7's "shown on alert board" half, which had no board to be shown on until this package.
    /// The CI fields are read live through the ports, so a rename reaches the board with no rewrite.
    /// </summary>
    [Fact]
    public async Task Alert_OnADeviceWithACi_CarriesItsCmdbContext()
    {
        var device = await CreateDeviceAsync();
        var alert = Alert(device, "context-rule", AlertSeverity.Critical, "Host is unreachable", DateTimeOffset.UtcNow);
        await WriteAlertsAsync(alert);

        var detail = await GetAsync<AlertDetailDto>($"/api/alerts/{alert.Id}");

        Assert.True(detail.Alert.CiFound);
        Assert.Equal(device.CiName, detail.Alert.CiName);
        Assert.Equal("NetworkDevice", detail.Alert.CiType);
        // The list carries the same CI fields, from one batched port read rather than one per row.
        var page = await GetAsync<AlertPageDto>($"/api/alerts?deviceId={device.Id}");
        Assert.Equal(device.CiName, Assert.Single(page.Items).CiName);
    }

    /// <summary>
    /// "Owner: —" on a CI that has been deleted reads as an unowned asset, which is a different fact.
    /// The board has to be able to say the CI is gone (WP-3.7's rule, applied to the list).
    /// </summary>
    [Fact]
    public async Task Alert_WhoseCiIsNotInTheCmdb_SaysSoRatherThanShowingBlankFields()
    {
        var device = await CreateDeviceAsync();
        var alert = Alert(device, "orphan-rule", AlertSeverity.Critical, "Host is unreachable", DateTimeOffset.UtcNow);
        alert.CiId = Guid.CreateVersion7();
        await WriteAlertsAsync(alert);

        var detail = await GetAsync<AlertDetailDto>($"/api/alerts/{alert.Id}");

        Assert.False(detail.Alert.CiFound);
        Assert.Null(detail.Alert.CiName);
    }

    /// <summary>Counts are the estate's, not the page's — a headline that moved when you turned a page.</summary>
    [Fact]
    public async Task Alerts_Counted_AreCountedOverEveryOpenAlertNotThePage()
    {
        var device = await CreateDeviceAsync();
        await WriteAlertsAsync(
            Alert(device, "count-critical", AlertSeverity.Critical, "One", DateTimeOffset.UtcNow),
            Alert(device, "count-warning", AlertSeverity.Warning, "Two", DateTimeOffset.UtcNow));

        var page = await GetAsync<AlertPageDto>($"/api/alerts?deviceId={device.Id}&pageSize=1");

        Assert.Single(page.Items);
        Assert.True(page.Counts.Critical >= 1);
        Assert.True(page.Counts.Warning >= 1);
        Assert.True(page.Counts.Unacknowledged >= 2);
    }

    // ---- acknowledgement ----

    [Fact]
    public async Task Acknowledge_AnOpenAlert_RecordsWhoAndBroadcastsIt()
    {
        var device = await CreateDeviceAsync();
        var alert = Alert(device, "ack-rule", AlertSeverity.Critical, "Host is unreachable", DateTimeOffset.UtcNow);
        await WriteAlertsAsync(alert);

        using var request = Authenticated(HttpMethod.Post, $"/api/alerts/{alert.Id}/acknowledgements");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var acknowledged = await response.Content.ReadFromJsonAsync<AlertDto>();
        Assert.NotNull(acknowledged!.AcknowledgedAt);
        Assert.Equal("alert-board-test-user-id", acknowledged.AcknowledgedBy);
        Assert.Equal("alert-board-test-user", acknowledged.AcknowledgedByName);

        // Every other board learns from the same push the engine uses — the WP's "ack reflects
        // everywhere" step, asserted rather than taken on trust.
        Assert.Contains(Broadcasts.Alerts, sent => sent.Id == alert.Id && sent.AcknowledgedAt is not null);
        Assert.Contains(Broadcasts.Tiles, tile => tile.DeviceId == device.Id);
    }

    /// <summary>Who claimed an incident and when is exactly what an after-action review asks for.</summary>
    [Fact]
    public async Task Acknowledge_AnOpenAlert_WritesAnAuditEntry()
    {
        var device = await CreateDeviceAsync();
        var alert = Alert(device, "audit-rule", AlertSeverity.Warning, "CPU is high", DateTimeOffset.UtcNow);
        await WriteAlertsAsync(alert);

        using var request = Authenticated(HttpMethod.Post, $"/api/alerts/{alert.Id}/acknowledgements");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entry = await platform.AuditEntries.SingleOrDefaultAsync(
            audit => audit.Action == "AlertAcknowledged" && audit.EntityId == alert.Id.ToString());

        Assert.NotNull(entry);
        Assert.Equal("alert-board-test-user-id", entry!.ActorId);
    }

    /// <summary>
    /// Acknowledging twice is not idempotent on purpose: the second press is somebody who could not
    /// see that a colleague already owned it, and silently overwriting the first name would hide
    /// exactly the fact they needed.
    /// </summary>
    [Fact]
    public async Task Acknowledge_AnAlreadyAcknowledgedAlert_IsRefusedNamingWhoHasIt()
    {
        var device = await CreateDeviceAsync();
        var alert = Alert(device, "twice-rule", AlertSeverity.Critical, "Host is unreachable", DateTimeOffset.UtcNow);
        await WriteAlertsAsync(alert);

        using var first = Authenticated(HttpMethod.Post, $"/api/alerts/{alert.Id}/acknowledgements");
        using var accepted = await _client!.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var second = Authenticated(HttpMethod.Post, $"/api/alerts/{alert.Id}/acknowledgements", "Admin");
        using var refused = await _client.SendAsync(second);
        var problem = await refused.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Already acknowledged by alert-board-test-user", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// There is nothing to deal with on an alert that has already cleared, and a recurrence opens a
    /// new row rather than reviving this one — so acknowledging history could only mislead.
    /// </summary>
    [Fact]
    public async Task Acknowledge_AClearedAlert_IsRefused()
    {
        var device = await CreateDeviceAsync();
        var alert = Cleared(Alert(device, "cleared-ack", AlertSeverity.Ok, "Recovered", DateTimeOffset.UtcNow));
        await WriteAlertsAsync(alert);

        using var request = Authenticated(HttpMethod.Post, $"/api/alerts/{alert.Id}/acknowledgements");
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already cleared", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acknowledge_AnAlertThatDoesNotExist_ReturnsNotFound()
    {
        using var request = Authenticated(
            HttpMethod.Post, $"/api/alerts/{Guid.CreateVersion7()}/acknowledgements");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Monitoring is an agent surface, like every other endpoint in the module.</summary>
    [Fact]
    public async Task Alerts_AsEndUser_AreForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/alerts", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StatusBoard_AsEndUser_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/monitoring/status-board", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- status board ----

    /// <summary>
    /// A freshly created device has never reported, so its tile is Unknown rather than Ok. Raising a
    /// Critical against it turns it red and puts that alert's own sentence on the tile.
    /// </summary>
    [Fact]
    public async Task StatusBoard_ForADeviceWithAndWithoutAlerts_ReportsTheWorstThingWrongWithIt()
    {
        var device = await CreateDeviceAsync();

        var quiet = await TileAsync(device.Id);
        Assert.Equal("Unknown", quiet.Status);
        Assert.Equal(0, quiet.OpenAlerts);

        await WriteAlertsAsync(
            Alert(device, "board-warning", AlertSeverity.Warning, "CPU is high", DateTimeOffset.UtcNow.AddMinutes(-9)),
            Alert(device, "board-critical", AlertSeverity.Critical, "Host is unreachable", DateTimeOffset.UtcNow.AddMinutes(-4)));

        var alerting = await TileAsync(device.Id);
        Assert.Equal("Critical", alerting.Status);
        Assert.Equal("Critical", alerting.Severity);
        Assert.Equal("Host is unreachable", alerting.Headline);
        Assert.Equal(2, alerting.OpenAlerts);
        Assert.Equal(1, alerting.CriticalAlerts);
        Assert.Equal(1, alerting.WarningAlerts);
    }

    /// <summary>The board lists the device it was searched for and finds it by address.</summary>
    [Fact]
    public async Task StatusBoard_SearchedByAddress_ReturnsTheMatchingDevice()
    {
        var device = await CreateDeviceAsync();

        var board = await GetAsync<StatusBoardDto>(
            $"/api/monitoring/status-board?search={Uri.EscapeDataString(device.Address)}");

        Assert.Equal(device.Id, Assert.Single(board.Items).DeviceId);
        Assert.True(board.Counts.Devices >= 1);
    }

    [Fact]
    public async Task StatusBoard_ForADeviceThatDoesNotExist_ReturnsNotFound()
    {
        using var request = Authenticated(
            HttpMethod.Get, $"/api/monitoring/status-board/{Guid.CreateVersion7()}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The migration this WP added is the only schema change; this is what catches an entity change
    /// that never made it into one.
    /// </summary>
    [Fact]
    public async Task MonitoringMigrations_CurrentModel_HasNoPendingChanges()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<MonitoringDbContext>()
            .Database.HasPendingModelChanges());
    }

    // ---- fixtures ----

    private static Alert Alert(
        DeviceDto device,
        string rule,
        AlertSeverity severity,
        string summary,
        DateTimeOffset raisedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            DeviceId = device.Id,
            CiId = device.CiId,
            CheckId = Guid.CreateVersion7(),
            // Unique per test run: a filtered unique index refuses two open alerts on one rule.
            RuleId = $"check:{Guid.NewGuid():N}:{rule}",
            MetricName = "check.success",
            Severity = severity,
            Status = AlertStatus.Open,
            Summary = summary,
            RaisedAt = raisedAt,
            LastObservedAt = raisedAt,
            PollerName = "poller-1",
        };

    private static Alert Cleared(Alert alert)
    {
        alert.Status = AlertStatus.Cleared;
        alert.Severity = AlertSeverity.Ok;
        alert.ClearedAt = alert.RaisedAt.AddMinutes(1);
        return alert;
    }

    private async Task WriteAlertsAsync(params Alert[] alerts)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        context.Alerts.AddRange(alerts);
        await context.SaveChangesAsync();
    }

    private Task<TileDto> TileAsync(Guid deviceId) =>
        GetAsync<TileDto>($"/api/monitoring/status-board/{deviceId}");

    private async Task<DeviceDto> CreateDeviceAsync()
    {
        var ci = await CreateCiAsync();
        using var request = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        request.Content = JsonContent.Create(new
        {
            ciId = ci.Id,
            address = $"10.40.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(1, 255)}",
            pollerGroup = $"board-{Guid.NewGuid():N}"[..20],
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedDeviceDto>();
        return new DeviceDto(created!.Id, created.CiId, created.Address, ci.Name);
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

    private async Task<T> GetAsync<T>(string uri, string role = "Technician")
    {
        using var request = Authenticated(HttpMethod.Get, uri, role);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(AlertBoardAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record CreatedDeviceDto(Guid Id, Guid CiId, string Address);

    private sealed record DeviceDto(Guid Id, Guid CiId, string Address, string CiName);

    private sealed record AlertDto(
        Guid Id,
        string Summary,
        string Severity,
        string Status,
        DateTimeOffset? AcknowledgedAt,
        string? AcknowledgedBy,
        string? AcknowledgedByName,
        bool CiFound,
        string? CiName,
        string? CiType);

    private sealed record AlertDetailDto(AlertDto Alert, List<object> OpenTickets);

    private sealed record AlertCountsDto(int Open, int Critical, int Warning, int Unacknowledged);

    private sealed record AlertPageDto(List<AlertDto> Items, int Total, AlertCountsDto Counts);

    private sealed record TileDto(
        Guid DeviceId,
        string Status,
        string Severity,
        int OpenAlerts,
        int CriticalAlerts,
        int WarningAlerts,
        int AcknowledgedAlerts,
        string? Headline);

    private sealed record StatusBoardCountsDto(int Devices, int Ok, int Warning, int Critical);

    private sealed record StatusBoardDto(List<TileDto> Items, int Total, StatusBoardCountsDto Counts);

    /// <summary>What the hub would have been told, captured instead of sent.</summary>
    private static class Broadcasts
    {
        public static readonly List<AlertResponse> Alerts = [];
        public static readonly List<DeviceStatusTile> Tiles = [];

        public static void Clear()
        {
            lock (Alerts) { Alerts.Clear(); Tiles.Clear(); }
        }
    }

    private sealed class RecordingBroadcaster : IMonitoringBroadcaster
    {
        public Task AlertChangedAsync(AlertResponse alert, CancellationToken cancellationToken)
        {
            lock (Broadcasts.Alerts) { Broadcasts.Alerts.Add(alert); }
            return Task.CompletedTask;
        }

        public Task DeviceStatusChangedAsync(DeviceStatusTile tile, CancellationToken cancellationToken)
        {
            lock (Broadcasts.Alerts) { Broadcasts.Tiles.Add(tile); }
            return Task.CompletedTask;
        }
    }

    private sealed class AlertBoardApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public AlertBoardApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
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
                    // No bus and no scheduler: this is the read surface and one write, none of which
                    // publishes. A second MassTransit host against the shared broker is what WP-3.2
                    // got bitten by.
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                // The hub is the host's and needs no database; what this asserts is that the push
                // happens at all, and with the right payload.
                services.Replace(ServiceDescriptor.Scoped<IMonitoringBroadcaster, RecordingBroadcaster>());
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = AlertBoardAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = AlertBoardAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = AlertBoardAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, AlertBoardAuthenticationHandler>(
                        AlertBoardAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
        }
    }

    private sealed class AlertBoardAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "AlertBoardTest";
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
                    new Claim(ClaimTypes.NameIdentifier, "alert-board-test-user-id"),
                    new Claim(ClaimTypes.Name, "alert-board-test-user"),
                    new Claim(ClaimTypes.Role, role),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
