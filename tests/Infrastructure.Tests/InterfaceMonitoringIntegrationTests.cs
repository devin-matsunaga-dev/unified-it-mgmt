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
using Modules.Helpdesk.Data;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;
using Modules.Monitoring.Features.Metrics;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// WP-4.5's chain against real infrastructure: a poller's interface samples become rows an operator
/// can read, series a chart can draw, and — three cycles later — one alert about one port.
/// <para>
/// The unit tests either side of this one prove the arithmetic and the rule matrix. What only a real
/// database can answer is whether the fold survives being written: that a second poll updates the
/// forty-eight rows rather than inserting forty-eight more, that the numbers reached the hypertable
/// as well as the row, and that deleting a device takes its interfaces with it.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class InterfaceMonitoringIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly InterfaceApplication _application;
    private HttpClient? _client;

    public InterfaceMonitoringIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new InterfaceApplication(
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
        // The alert engine's enrichment asks Helpdesk what is already open for the CI on every
        // publication, and an unmigrated helpdesk schema answers 42P01 as a 500 from a query that
        // mentions neither interfaces nor alerts. The sixth package to meet that trap.
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- storage and the read API ----

    [Fact]
    public async Task Interfaces_AfterOnePoll_AreReadableWithTheirNamesStatusesAndRates()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();

        await IngestAsync(Poll(fixture, Base, [
            Port(1, "Gi0/1", alias: "uplink to core", oper: 1, admin: 1, bitsIn: 12_500_000, utilisation: 1.25),
            Port(2, "Gi0/2", oper: 2, admin: 1),
        ]));

        var interfaces = await GetAsync<List<InterfaceDto>>(
            $"/api/monitored-devices/{fixture.DeviceId}/interfaces");

        Assert.Equal([1, 2], interfaces.Select(link => link.IfIndex));
        var uplink = interfaces[0];
        Assert.Equal("Gi0/1", uplink.Name);
        Assert.Equal("uplink to core", uplink.Alias);
        Assert.Equal("Up", uplink.OperStatus);
        Assert.Equal(1_000_000_000, uplink.SpeedBitsPerSecond);
        Assert.Equal(12_500_000, uplink.BitsInPerSecond);
        Assert.Equal(1.25, uplink.UtilisationPercent);
        Assert.Equal(fixture.CheckId, uplink.CheckId);
        // What the browser prepends to a field name to chart this port, so the shape of a metric
        // name stays knowledge one module holds.
        Assert.Equal("interface.1.", uplink.MetricPrefix);
        Assert.Equal("Down", interfaces[1].OperStatus);
    }

    /// <summary>
    /// The row is current state and the hypertable is history, and both have to happen: without the
    /// second, the interface table would be a live view with no chart behind it.
    /// </summary>
    [Fact]
    public async Task Interfaces_EveryNumberReported_IsAlsoASeriesTheChartCanDraw()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();

        await IngestAsync(Poll(fixture, Base, [Port(1, "Gi0/1", oper: 1, admin: 1, bitsIn: 1_000)]));
        await IngestAsync(Poll(fixture, Base.AddMinutes(1),
            [Port(1, "Gi0/1", oper: 1, admin: 1, bitsIn: 2_000)]));

        var series = await GetAsync<SeriesDto>(
            $"/api/monitored-devices/{fixture.DeviceId}/metrics/series"
            + $"?metric=interface.1.bits_in_per_second&checkId={fixture.CheckId}"
            + $"&from={Uri.EscapeDataString(Base.AddMinutes(-5).ToString("O"))}"
            + $"&to={Uri.EscapeDataString(Base.AddMinutes(5).ToString("O"))}"
            + "&resolution=Raw");

        Assert.Equal([1_000, 2_000], series.Points.Select(point => point.Value));
    }

    /// <summary>
    /// A switch reports its ports every cycle forever. If this inserted instead of updating, a
    /// 48-port switch on a 60-second check would add 69,120 rows a day and the interface table would
    /// grow a duplicate of every port per poll.
    /// </summary>
    [Fact]
    public async Task Interfaces_PolledTwice_AreUpdatedInPlaceRatherThanDuplicated()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();

        await IngestAsync(Poll(fixture, Base, [Port(1, "Gi0/1", oper: 1, admin: 1, bitsIn: 1_000)]));
        await IngestAsync(Poll(fixture, Base.AddMinutes(1),
            [Port(1, "TenGig0/1", oper: 2, admin: 1, bitsIn: 5_000)]));

        var link = Assert.Single(await GetAsync<List<InterfaceDto>>(
            $"/api/monitored-devices/{fixture.DeviceId}/interfaces"));
        // Renamed on the switch, and renamed here on the next poll: the identity fields travel every
        // cycle precisely so nobody has to send an event when somebody relabels a port.
        Assert.Equal("TenGig0/1", link.Name);
        Assert.Equal("Down", link.OperStatus);
        Assert.Equal(5_000, link.BitsInPerSecond);
    }

    /// <summary>A redelivered batch must not restore a port's old state over the one it reports now.</summary>
    [Fact]
    public async Task Interfaces_AnOlderBatchArrivingLate_DoesNotOverwriteTheCurrentState()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();

        await IngestAsync(Poll(fixture, Base.AddMinutes(5), [Port(1, "Gi0/1", oper: 1, admin: 1)]));
        await IngestAsync(Poll(fixture, Base, [Port(1, "Gi0/1", oper: 2, admin: 1)]));

        var link = Assert.Single(await GetAsync<List<InterfaceDto>>(
            $"/api/monitored-devices/{fixture.DeviceId}/interfaces"));
        Assert.Equal("Up", link.OperStatus);
    }

    /// <summary>
    /// An interface is a property of one device rather than a fact about two peers, so it goes when
    /// the device does — WP-3.1's checks-and-devices rule, applied one level down.
    /// </summary>
    [Fact]
    public async Task DeletingADevice_TakesItsInterfacesWithIt()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();
        await IngestAsync(Poll(fixture, Base, [Port(1, "Gi0/1", oper: 1, admin: 1)]));

        using var request = Authenticated(HttpMethod.Delete, $"/api/monitored-devices/{fixture.DeviceId}");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        Assert.Empty(await context.DeviceInterfaces
            .Where(link => link.DeviceId == fixture.DeviceId).ToListAsync());
    }

    /// <summary>
    /// The failure path. Null rather than an empty list from the service, so a device that does not
    /// exist is a 404 and a switch nobody polls for interfaces is an empty table.
    /// </summary>
    [Fact]
    public async Task Interfaces_ForADeviceThatDoesNotExist_Is404()
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/monitored-devices/{Guid.CreateVersion7()}/interfaces");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal("Monitored device not found.", problem!.Title);
    }

    [Fact]
    public async Task Interfaces_ForADeviceThatHasNeverReportedAny_IsAnEmptyList() =>
        Assert.Empty(await GetAsync<List<InterfaceDto>>(
            $"/api/monitored-devices/{(await CreateDeviceWithInterfaceCheckAsync()).DeviceId}/interfaces"));

    // ---- alerting, per port ----

    /// <summary>
    /// The WP's second verification step, minus the simulator: a port goes down, and after the
    /// platform's three sustained cycles exactly one alert is open — on that port, naming it, and
    /// with the other three ports of the same switch silent.
    /// </summary>
    [Fact]
    public async Task Alerting_APortThatGoesDown_RaisesOneCriticalAlertNamingIt()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await EvaluateAsync(Poll(fixture, Base.AddMinutes(cycle), [
                Port(1, "Gi0/1", alias: "uplink to core", oper: 1, admin: 1, utilisation: 4),
                Port(2, "Gi0/2", alias: "server room patch", oper: 2, admin: 1, utilisation: 0),
                // Administratively shut: an estate is full of these and none of them is a fault.
                Port(3, "Gi0/3", oper: 2, admin: 2),
            ]));
        }

        var alert = Assert.Single(await OpenAlertsAsync(fixture.DeviceId));
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal($"check:{fixture.CheckId}:if:2:oper-status", alert.RuleId);
        Assert.Equal("interface.2.oper_status", alert.MetricName);
        Assert.Equal("Gi0/2 (server room patch) on 10.40.0.1 is down.", alert.Summary);
    }

    /// <summary>
    /// One port past its threshold is one alert about that port, and the check's thresholds are what
    /// it is judged against — an interface check has no rule of its own.
    /// </summary>
    [Fact]
    public async Task Alerting_APortOverTheUtilisationThreshold_RaisesOneAlertOnThatPort()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await EvaluateAsync(Poll(fixture, Base.AddMinutes(cycle), [
                Port(1, "Gi0/1", oper: 1, admin: 1, utilisation: 12),
                Port(2, "Gi0/2", oper: 1, admin: 1, utilisation: 96),
            ]));
        }

        var alert = Assert.Single(await OpenAlertsAsync(fixture.DeviceId));
        Assert.Equal($"check:{fixture.CheckId}:if:2:utilisation", alert.RuleId);
        Assert.Equal(96d, alert.LastValue);
        Assert.Equal(90d, alert.Threshold);
    }

    /// <summary>
    /// The recovery half, and the reason the rule id has to be identical across cycles: a port that
    /// comes back clears the alert it raised rather than leaving it open beside a healthy port.
    /// </summary>
    [Fact]
    public async Task Alerting_APortThatComesBack_ClearsItsOwnAlert()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await EvaluateAsync(Poll(fixture, Base.AddMinutes(cycle),
                [Port(2, "Gi0/2", oper: 2, admin: 1)]));
        }

        for (var cycle = 3; cycle < 6; cycle++)
        {
            await EvaluateAsync(Poll(fixture, Base.AddMinutes(cycle),
                [Port(2, "Gi0/2", oper: 1, admin: 1)]));
        }

        Assert.Empty(await OpenAlertsAsync(fixture.DeviceId));
        var cleared = Assert.Single(await AlertsAsync(fixture.DeviceId));
        Assert.Equal(AlertStatus.Cleared, cleared.Status);
    }

    /// <summary>
    /// The failure path that matters most, because it is the difference between this feature being
    /// usable and one dead switch being forty-eight tickets: a check that failed carries no samples,
    /// so the device's availability rule fires and its ports say nothing at all.
    /// </summary>
    [Fact]
    public async Task Alerting_WhenTheWholeDeviceStopsAnswering_RaisesOneAvailabilityAlertAndNoPortAlerts()
    {
        var fixture = await CreateDeviceWithInterfaceCheckAsync();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await EvaluateAsync(Poll(fixture, Base.AddMinutes(cycle), [
                Port(1, "Gi0/1", oper: 1, admin: 1),
                Port(2, "Gi0/2", oper: 1, admin: 1),
            ]));
        }

        for (var cycle = 3; cycle < 6; cycle++)
        {
            await EvaluateAsync(Failed(fixture, Base.AddMinutes(cycle)));
        }

        var alert = Assert.Single(await OpenAlertsAsync(fixture.DeviceId));
        Assert.Equal($"check:{fixture.CheckId}:availability", alert.RuleId);
    }

    // ---- helpers ----

    private static IReadOnlyList<MetricSample> Port(
        int ifIndex,
        string name,
        double oper,
        double admin,
        string? alias = null,
        double? bitsIn = null,
        double? utilisation = null)
    {
        var samples = new List<MetricSample>
        {
            new($"interface.{ifIndex}.name", null, name, null),
            new($"interface.{ifIndex}.oper_status", oper, null, null),
            new($"interface.{ifIndex}.admin_status", admin, null, null),
            new($"interface.{ifIndex}.speed_bits_per_second", 1_000_000_000, null, "bit/s"),
        };
        if (alias is not null) samples.Add(new($"interface.{ifIndex}.alias", null, alias, null));
        if (bitsIn is not null) samples.Add(new($"interface.{ifIndex}.bits_in_per_second", bitsIn, null, "bit/s"));
        if (utilisation is not null)
        {
            samples.Add(new($"interface.{ifIndex}.utilisation_percent", utilisation, null, "%"));
        }

        return samples;
    }

    private static DeviceTelemetryReported Poll(
        DeviceFixture fixture,
        DateTimeOffset observedAt,
        IReadOnlyList<IReadOnlyList<MetricSample>> ports) => new(
        Guid.CreateVersion7(), observedAt, "poller-1", "default", CycleNumber: 1,
        [
            new DeviceCheckResult(
                fixture.DeviceId, fixture.CiId, fixture.CheckId, "Snmp", "SNMP: interfaces",
                fixture.Address, observedAt, Succeeded: true, LatencyMs: 18, Error: null,
                Metrics: [.. ports.SelectMany(port => port)]),
        ]);

    private static DeviceTelemetryReported Failed(DeviceFixture fixture, DateTimeOffset observedAt) => new(
        Guid.CreateVersion7(), observedAt, "poller-1", "default", CycleNumber: 1,
        [
            new DeviceCheckResult(
                fixture.DeviceId, fixture.CiId, fixture.CheckId, "Snmp", "SNMP: interfaces",
                fixture.Address, observedAt, Succeeded: false, LatencyMs: null,
                Error: "SNMP interfaces against 10.40.0.1:161 failed: timed out", Metrics: []),
        ]);

    private async Task IngestAsync(DeviceTelemetryReported telemetry)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMetricIngestionService>()
            .IngestAsync(telemetry, CancellationToken.None);
    }

    private async Task EvaluateAsync(DeviceTelemetryReported telemetry)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAlertEngine>()
            .EvaluateAsync(telemetry, CancellationToken.None);
    }

    private async Task<IReadOnlyList<Alert>> AlertsAsync(Guid deviceId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        return await context.Alerts
            .Where(alert => alert.DeviceId == deviceId)
            .OrderBy(alert => alert.RaisedAt)
            .ToListAsync();
    }

    private async Task<IReadOnlyList<Alert>> OpenAlertsAsync(Guid deviceId)
    {
        var alerts = await AlertsAsync(deviceId);
        return [.. alerts.Where(alert => alert.Status == AlertStatus.Open)];
    }

    private async Task<DeviceFixture> CreateDeviceWithInterfaceCheckAsync()
    {
        var ci = await CreateCiAsync();
        var device = await CreateDeviceAsync(ci.Id);

        using var request = Authenticated(HttpMethod.Post, $"/api/monitored-devices/{device.Id}/checks");
        request.Content = JsonContent.Create(new
        {
            type = "Snmp",
            name = "SNMP: interfaces",
            intervalSeconds = 60,
            timeoutSeconds = 5,
            warningThreshold = 70d,
            criticalThreshold = 90d,
            comparison = "GreaterThan",
            // No `oid`, which is the point: WP-4.5 narrowed WP-3.1's rule so that a check naming a
            // metric family no longer has to carry an OID it never reads.
            parameters = new Dictionary<string, string> { ["metric"] = "interfaces" },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var check = await response.Content.ReadFromJsonAsync<CheckDto>();

        return new DeviceFixture(device.Id, device.CiId, check!.Id, device.Address);
    }

    private async Task<DeviceDto> CreateDeviceAsync(Guid ciId)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        request.Content = JsonContent.Create(new
        {
            ciId,
            address = "10.40.0.1",
            pollerGroup = $"group-{Guid.NewGuid():N}"[..20],
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
                ["managementIp"] = "10.40.0.1",
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
        request.Headers.Add(InterfaceAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record DeviceFixture(Guid DeviceId, Guid CiId, Guid CheckId, string Address);

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record DeviceDto(Guid Id, Guid CiId, string Address, string PollerGroup);

    private sealed record CheckDto(Guid Id, string Name);

    private sealed record ProblemDto(string Title, int Status);

    /// <summary>
    /// The series response as the wire carries it. Read into a local shape rather than the module's
    /// own record because the API serialises its enums as strings and the record's are enums — the
    /// browser reads the same JSON this does.
    /// </summary>
    private sealed record SeriesDto(string Metric, Guid? CheckId, string Unit, List<SeriesPointDto> Points);

    private sealed record SeriesPointDto(DateTimeOffset Timestamp, double Value);

    private sealed record InterfaceDto(
        int IfIndex,
        string Name,
        string? Alias,
        string? MacAddress,
        int? InterfaceType,
        string AdminStatus,
        string OperStatus,
        long? SpeedBitsPerSecond,
        double? BitsInPerSecond,
        double? BitsOutPerSecond,
        double? UtilisationPercent,
        double? ErrorsInPerSecond,
        double? ErrorsOutPerSecond,
        double? DiscardsInPerSecond,
        double? DiscardsOutPerSecond,
        Guid CheckId,
        string MetricPrefix,
        DateTimeOffset ObservedAt);

    private sealed class InterfaceApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _redisConnectionString;
        private readonly string _minioConnectionString;

        public InterfaceApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string redisConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _redisConnectionString = redisConnectionString;
            _minioConnectionString = minioConnectionString;
            // Environment variables as well as configuration: Aspire's AddNpgsqlDataSource and
            // AddRedisClient read the builder's configuration while the host is being built, which
            // is before WebApplicationFactory's own sources exist.
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
                    // The engine is driven directly, so its events stay in the outbox rather than
                    // standing a second MassTransit host beside the one the bus tests own — and, per
                    // WP-3.12's finding, rather than sweeping other classes' outbox rows away.
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = InterfaceAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = InterfaceAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = InterfaceAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, InterfaceAuthenticationHandler>(
                        InterfaceAuthenticationHandler.TestScheme,
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

    private sealed class InterfaceAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "InterfaceTest";
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
                    new Claim(ClaimTypes.NameIdentifier, "interface-test-user-id"),
                    new Claim(ClaimTypes.Name, "interface-test-user"),
                    new Claim(ClaimTypes.Role, role),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
