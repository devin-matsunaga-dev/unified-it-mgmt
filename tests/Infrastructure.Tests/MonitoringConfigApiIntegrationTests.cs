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
using Modules.Monitoring.Data;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The WP's verification chain: create a device and its checks through the API, fetch them from the
/// config endpoint, then edit a threshold and watch the config version move.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class MonitoringConfigApiIntegrationTests : IAsyncLifetime
{
    private readonly MonitoringApplication _application;
    private HttpClient? _client;

    public MonitoringConfigApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new MonitoringApplication(
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The WP's first two verification steps in one pass.</summary>
    [Fact]
    public async Task PollerConfig_AfterCreatingADeviceAndChecks_ReturnsThem()
    {
        var pollerGroup = NewGroup();
        var device = await CreateDeviceAsync(pollerGroup, "10.20.0.1");
        await CreateCheckAsync(device.Id, "Icmp", "Reachability", intervalSeconds: 60, timeoutSeconds: 5);
        await CreateCheckAsync(device.Id, "Snmp", "CPU", intervalSeconds: 300, timeoutSeconds: 10,
            warningThreshold: 80, criticalThreshold: 95,
            parameters: new() { ["oid"] = "1.3.6.1.4.1.9.9.109.1.1.1.1.7.1" });

        var poller = await RegisterPollerAsync(NewPollerName(), pollerGroup);
        var config = await GetAsync<PollerConfigDto>($"/api/pollers/{poller.Name}/config");

        Assert.True(config.IsFullSnapshot);
        Assert.Equal(pollerGroup, config.PollerGroup);
        var configured = Assert.Single(config.Devices);
        Assert.Equal(device.Id, configured.DeviceId);
        Assert.Equal("10.20.0.1", configured.Address);
        Assert.Equal(["CPU", "Reachability"], configured.Checks.Select(check => check.Name).Order());

        var cpu = configured.Checks.Single(check => check.Name == "CPU");
        Assert.Equal("Snmp", cpu.Type);
        Assert.Equal(300, cpu.IntervalSeconds);
        Assert.Equal(95, cpu.CriticalThreshold);
        Assert.Equal("1.3.6.1.4.1.9.9.109.1.1.1.1.7.1", cpu.Parameters["oid"]);
    }

    /// <summary>The WP's third verification step.</summary>
    [Fact]
    public async Task EditingACheckThreshold_BumpsTheConfigVersion()
    {
        var pollerGroup = NewGroup();
        var device = await CreateDeviceAsync(pollerGroup, "10.20.0.2");
        var check = await CreateCheckAsync(device.Id, "Snmp", "CPU", 300, 10,
            warningThreshold: 80, criticalThreshold: 95,
            parameters: new() { ["oid"] = "1.3.6.1.2.1.1.3.0" });

        var poller = await RegisterPollerAsync(NewPollerName(), pollerGroup);
        var before = await GetAsync<PollerConfigDto>($"/api/pollers/{poller.Name}/config");

        using var edit = Authenticated(HttpMethod.Put, $"/api/checks/{check.Id}");
        edit.Content = JsonContent.Create(new
        {
            name = "CPU",
            intervalSeconds = 300,
            timeoutSeconds = 10,
            warningThreshold = 70,
            criticalThreshold = 90,
            comparison = "GreaterThan",
            parameters = new Dictionary<string, string> { ["oid"] = "1.3.6.1.2.1.1.3.0" },
            isEnabled = true,
        });
        using var edited = await _client!.SendAsync(edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = await GetAsync<PollerConfigDto>(
            $"/api/pollers/{poller.Name}/config?sinceVersion={before.ConfigVersion}");

        Assert.True(after.ConfigVersion > before.ConfigVersion);
        Assert.False(after.IsFullSnapshot);
        var resent = Assert.Single(after.Devices);
        Assert.Equal(device.Id, resent.DeviceId);
        Assert.Equal(90, resent.Checks.Single(item => item.Name == "CPU").CriticalThreshold);
    }

    /// <summary>
    /// The point of the version: a steady-state fetch returns nothing at all, and one edit returns
    /// exactly the device that changed rather than the whole estate.
    /// </summary>
    [Fact]
    public async Task PollerConfig_WithNothingChanged_ReturnsAnEmptyDelta()
    {
        var pollerGroup = NewGroup();
        await CreateDeviceAsync(pollerGroup, "10.20.0.3");
        var quiet = await CreateDeviceAsync(pollerGroup, "10.20.0.4");

        var poller = await RegisterPollerAsync(NewPollerName(), pollerGroup);
        var snapshot = await GetAsync<PollerConfigDto>($"/api/pollers/{poller.Name}/config");
        Assert.Equal(2, snapshot.Devices.Count);

        var unchanged = await GetAsync<PollerConfigDto>(
            $"/api/pollers/{poller.Name}/config?sinceVersion={snapshot.ConfigVersion}");

        Assert.Empty(unchanged.Devices);
        Assert.Empty(unchanged.RemovedDeviceIds);
        Assert.Equal(snapshot.ConfigVersion, unchanged.ConfigVersion);

        using var edit = Authenticated(HttpMethod.Put, $"/api/monitored-devices/{quiet.Id}");
        edit.Content = JsonContent.Create(new { address = "10.20.0.44", pollerGroup, isEnabled = true });
        using var edited = await _client!.SendAsync(edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var delta = await GetAsync<PollerConfigDto>(
            $"/api/pollers/{poller.Name}/config?sinceVersion={snapshot.ConfigVersion}");

        var only = Assert.Single(delta.Devices);
        Assert.Equal(quiet.Id, only.DeviceId);
        Assert.Equal("10.20.0.44", only.Address);
    }

    [Fact]
    public async Task PollerConfig_AfterADeviceIsDeleted_ReportsItRemoved()
    {
        var pollerGroup = NewGroup();
        var device = await CreateDeviceAsync(pollerGroup, "10.20.0.5");
        var poller = await RegisterPollerAsync(NewPollerName(), pollerGroup);
        var snapshot = await GetAsync<PollerConfigDto>($"/api/pollers/{poller.Name}/config");

        using var delete = Authenticated(HttpMethod.Delete, $"/api/monitored-devices/{device.Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var delta = await GetAsync<PollerConfigDto>(
            $"/api/pollers/{poller.Name}/config?sinceVersion={snapshot.ConfigVersion}");

        Assert.Empty(delta.Devices);
        Assert.Equal([device.Id], delta.RemovedDeviceIds);
    }

    /// <summary>Deleting a device takes its checks with it — a check without its device is nothing.</summary>
    [Fact]
    public async Task DeletingADevice_CascadesItsChecks()
    {
        var device = await CreateDeviceAsync(NewGroup(), "10.20.0.6");
        var check = await CreateCheckAsync(device.Id, "Icmp", "Reachability", 60, 5);

        using var delete = Authenticated(HttpMethod.Delete, $"/api/monitored-devices/{device.Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        Assert.Null(await context.CheckDefinitions.SingleOrDefaultAsync(item => item.Id == check.Id));
    }

    [Fact]
    public async Task MaintenanceWindow_ScopedToADevice_ReachesThePollerConfig()
    {
        var pollerGroup = NewGroup();
        var device = await CreateDeviceAsync(pollerGroup, "10.20.0.7");

        using var request = Authenticated(HttpMethod.Post, "/api/maintenance-windows");
        request.Content = JsonContent.Create(new
        {
            name = "Quarterly power test",
            startsAt = DateTimeOffset.UtcNow.AddHours(1),
            endsAt = DateTimeOffset.UtcNow.AddHours(3),
            deviceIds = new[] { device.Id },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var window = Assert.IsType<MaintenanceWindowDto>(
            await response.Content.ReadFromJsonAsync<MaintenanceWindowDto>());
        Assert.Equal("Scheduled", window.Status);

        var poller = await RegisterPollerAsync(NewPollerName(), pollerGroup);
        var config = await GetAsync<PollerConfigDto>($"/api/pollers/{poller.Name}/config");

        var configured = Assert.Single(config.MaintenanceWindows, item => item.Id == window.Id);
        Assert.False(configured.AppliesToAllDevices);
        Assert.Equal([device.Id], configured.DeviceIds);
    }

    [Fact]
    public async Task CreateDevice_WritesAnAuditEntry()
    {
        var device = await CreateDeviceAsync(NewGroup(), "10.20.0.8");

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Contains(
            await context.AuditEntries.ToListAsync(),
            entry => entry.EntityType == "MonitoredDevice"
                && entry.EntityId == device.Id.ToString()
                && entry.Action == "Created");
    }

    /// <summary>Registration is an upsert: a restarted poller is the same poller.</summary>
    [Fact]
    public async Task RegisterPoller_Twice_KeepsOneRegistration()
    {
        var name = NewPollerName();
        var first = await RegisterPollerAsync(name, NewGroup());
        var second = await RegisterPollerAsync(name, NewGroup(), agentVersion: "1.1.0");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("1.1.0", second.AgentVersion);
        Assert.True(second.LastRegisteredAt >= first.LastRegisteredAt);

        // The original registration instant survives the re-registration rather than being reset.
        // Compared with a tolerance, not for equality: the first response carries the in-memory
        // DateTimeOffset at full 100ns tick precision while the second is read back from a Postgres
        // timestamptz, which keeps microseconds — so an exact compare fails roughly nine times in ten.
        // A millisecond is far below the gap between two HTTP round trips and far above that
        // truncation, so it still distinguishes "preserved" from "reset to now".
        Assert.True(
            (second.RegisteredAt - first.RegisteredAt).Duration() < TimeSpan.FromMilliseconds(1),
            $"Registration instant moved: {first.RegisteredAt:O} became {second.RegisteredAt:O}.");
        Assert.True(second.RegisteredAt < second.LastRegisteredAt);
    }

    // ---- failure paths ----

    /// <summary>A device is the monitoring of a CI, so there has to be a CI.</summary>
    [Fact]
    public async Task CreateDevice_ForACiThatDoesNotExist_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        request.Content = JsonContent.Create(new { ciId = Guid.CreateVersion7(), address = "10.20.0.9" });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDevice_ForACiAlreadyMonitored_ReturnsConflict()
    {
        var ci = await CreateCiAsync();
        using var first = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        first.Content = JsonContent.Create(new { ciId = ci.Id, address = "10.20.0.10" });
        using var created = await _client!.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var second = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        second.Content = JsonContent.Create(new { ciId = ci.Id, address = "10.20.0.11" });
        using var conflict = await _client!.SendAsync(second);
        var problem = await conflict.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("already monitored", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCheck_WithATimeoutLongerThanItsInterval_ReturnsValidationProblem()
    {
        var device = await CreateDeviceAsync(NewGroup(), "10.20.0.12");

        using var request = Authenticated(HttpMethod.Post, $"/api/monitored-devices/{device.Id}/checks");
        request.Content = JsonContent.Create(new
        {
            type = "Icmp",
            name = "Reachability",
            intervalSeconds = 30,
            timeoutSeconds = 60,
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("shorter than the interval", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A poller holding a version this server never issued is reading someone else's history. It is
    /// refused rather than handed a delta computed against the wrong past.
    /// </summary>
    [Fact]
    public async Task PollerConfig_WithASinceVersionAheadOfTheServer_ReturnsValidationProblem()
    {
        var poller = await RegisterPollerAsync(NewPollerName(), NewGroup());
        var config = await GetAsync<PollerConfigDto>($"/api/pollers/{poller.Name}/config");

        using var request = Authenticated(
            HttpMethod.Get, $"/api/pollers/{poller.Name}/config?sinceVersion={config.ConfigVersion + 1_000}");
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ahead of the current configuration version", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollerConfig_ForAnUnregisteredPoller_ReturnsNotFound()
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/pollers/{NewPollerName()}/config");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MaintenanceWindow_EndingBeforeItStarts_ReturnsValidationProblem()
    {
        var device = await CreateDeviceAsync(NewGroup(), "10.20.0.13");

        using var request = Authenticated(HttpMethod.Post, "/api/maintenance-windows");
        request.Content = JsonContent.Create(new
        {
            name = "Backwards window",
            startsAt = DateTimeOffset.UtcNow.AddHours(3),
            endsAt = DateTimeOffset.UtcNow.AddHours(1),
            deviceIds = new[] { device.Id },
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must end after it starts", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Naming devices on an estate-wide window is refused rather than ignored — silently dropping the
    /// list would leave the operator believing they had scoped it.
    /// </summary>
    [Fact]
    public async Task MaintenanceWindow_EstateWideWithADeviceList_ReturnsValidationProblem()
    {
        var device = await CreateDeviceAsync(NewGroup(), "10.20.0.14");

        using var request = Authenticated(HttpMethod.Post, "/api/maintenance-windows");
        request.Content = JsonContent.Create(new
        {
            name = "Contradictory window",
            startsAt = DateTimeOffset.UtcNow.AddHours(1),
            endsAt = DateTimeOffset.UtcNow.AddHours(2),
            appliesToAllDevices = true,
            deviceIds = new[] { device.Id },
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("covers every device", problem, StringComparison.Ordinal);
    }

    /// <summary>Monitoring is an agent surface, like the CMDB.</summary>
    [Fact]
    public async Task MonitoredDevices_AsEndUser_AreForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/monitored-devices", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MonitoredDevice_ThatDoesNotExist_ReturnsNotFound()
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/monitored-devices/{Guid.CreateVersion7()}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- fixtures ----

    private static string NewGroup() => $"group-{Guid.NewGuid():N}"[..20];

    private static string NewPollerName() => $"poller-{Guid.NewGuid():N}"[..20];

    private async Task<DeviceDto> CreateDeviceAsync(string pollerGroup, string address)
    {
        var ci = await CreateCiAsync();
        using var request = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        request.Content = JsonContent.Create(new { ciId = ci.Id, address, pollerGroup });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<DeviceDto>(await response.Content.ReadFromJsonAsync<DeviceDto>());
    }

    private async Task<CheckDto> CreateCheckAsync(
        Guid deviceId,
        string type,
        string name,
        int intervalSeconds,
        int timeoutSeconds,
        double? warningThreshold = null,
        double? criticalThreshold = null,
        Dictionary<string, string>? parameters = null)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/monitored-devices/{deviceId}/checks");
        request.Content = JsonContent.Create(new
        {
            type,
            name,
            intervalSeconds,
            timeoutSeconds,
            warningThreshold,
            criticalThreshold,
            parameters,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CheckDto>(await response.Content.ReadFromJsonAsync<CheckDto>());
    }

    private async Task<PollerDto> RegisterPollerAsync(
        string name,
        string pollerGroup,
        string? agentVersion = null)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/pollers/registrations");
        request.Content = JsonContent.Create(new { name, pollerGroup, agentVersion });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<PollerDto>(await response.Content.ReadFromJsonAsync<PollerDto>());
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
        request.Headers.Add(MonitoringAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record DeviceDto(
        Guid Id,
        Guid CiId,
        string? CiName,
        string Address,
        string PollerGroup,
        bool IsEnabled,
        int CheckCount);

    private sealed record CheckDto(
        Guid Id,
        Guid DeviceId,
        string Type,
        string Name,
        int IntervalSeconds,
        int TimeoutSeconds,
        double? WarningThreshold,
        double? CriticalThreshold,
        Dictionary<string, string> Parameters,
        bool IsEnabled);

    private sealed record PollerDto(
        Guid Id,
        string Name,
        string PollerGroup,
        string? AgentVersion,
        long LastConfigVersion,
        DateTimeOffset RegisteredAt,
        DateTimeOffset LastRegisteredAt,
        long CurrentConfigVersion);

    private sealed record MaintenanceWindowDto(
        Guid Id,
        string Name,
        bool AppliesToAllDevices,
        List<Guid> DeviceIds,
        string Status);

    private sealed record PollerConfigDeviceDto(
        Guid DeviceId,
        Guid CiId,
        string? CiName,
        string Address,
        List<PollerConfigCheckDto> Checks);

    private sealed record PollerConfigCheckDto(
        Guid CheckId,
        string Type,
        string Name,
        int IntervalSeconds,
        int TimeoutSeconds,
        double? WarningThreshold,
        double? CriticalThreshold,
        Dictionary<string, string> Parameters);

    private sealed record PollerConfigWindowDto(
        Guid Id,
        string Name,
        bool AppliesToAllDevices,
        List<Guid> DeviceIds);

    private sealed record PollerConfigDto(
        string PollerName,
        string PollerGroup,
        long ConfigVersion,
        bool IsFullSnapshot,
        List<PollerConfigDeviceDto> Devices,
        List<Guid> RemovedDeviceIds,
        List<PollerConfigWindowDto> MaintenanceWindows);

    private sealed class MonitoringApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public MonitoringApplication(
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
                        options.DefaultAuthenticateScheme = MonitoringAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = MonitoringAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = MonitoringAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, MonitoringAuthenticationHandler>(
                        MonitoringAuthenticationHandler.TestScheme,
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

    private sealed class MonitoringAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "MonitoringTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "monitoring-test-user-id"),
                    new Claim(ClaimTypes.Name, "monitoring-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
