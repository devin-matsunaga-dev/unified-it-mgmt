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
using Modules.Monitoring.Features.Heartbeats;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// WP-3.2's two halves on the platform side: the poller's own credential replacing the interim
/// operator policy, and the heartbeat that credential exists to carry.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PollerHeartbeatApiIntegrationTests : IAsyncLifetime
{
    private const string PollerRole = "Poller";
    private const string TechnicianRole = "Technician";

    private readonly HeartbeatApplication _application;
    private HttpClient? _client;

    public PollerHeartbeatApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new HeartbeatApplication(
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

    // ---- the poller's own credential ----

    [Fact]
    public async Task Registration_WithThePollersOwnCredential_Succeeds()
    {
        var poller = await RegisterAsync(NewPollerName());

        Assert.NotEqual(Guid.Empty, poller.Id);
        Assert.Null(poller.LastHeartbeatAt);
    }

    /// <summary>
    /// The interim is over: a Technician token used to reach this, and WP-3.1 recorded that as
    /// wrong. A poller must not need an agent's rights, and an agent has no business registering one.
    /// </summary>
    [Fact]
    public async Task Registration_WithAnOperatorToken_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/pollers/registrations", TechnicianRole);
        request.Content = JsonContent.Create(new { name = NewPollerName(), pollerGroup = "default" });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PollerConfig_WithAnOperatorToken_IsForbidden()
    {
        var poller = await RegisterAsync(NewPollerName());

        using var request = Authenticated(
            HttpMethod.Get, $"/api/pollers/{poller.Name}/config", TechnicianRole);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The reverse also holds: the fleet list is an operator's view, and the poller's credential is
    /// not a way into it. The two policies are disjoint on purpose.
    /// </summary>
    [Fact]
    public async Task PollerList_WithThePollersOwnCredential_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/pollers", PollerRole);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PollerList_WithAnOperatorToken_Succeeds()
    {
        await RegisterAsync(NewPollerName());

        using var request = Authenticated(HttpMethod.Get, "/api/pollers", TechnicianRole);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Registration_WithNoTokenAtAll_IsUnauthorized()
    {
        using var response = await _client!.PostAsJsonAsync(
            "/api/pollers/registrations", new { name = NewPollerName() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- the heartbeat ----

    [Fact]
    public async Task Heartbeat_FromARegisteredPoller_IsRecordedAgainstIt()
    {
        var poller = await RegisterAsync(NewPollerName());
        var beatAt = DateTimeOffset.UtcNow;

        Assert.True(await RecordAsync(Heartbeat(poller.Name, beatAt, cycleNumber: 7, deviceCount: 3)));

        var stored = await ReadAsync(poller.Name);
        Assert.NotNull(stored.LastHeartbeatAt);
        Assert.Equal(15, stored.HeartbeatIntervalSeconds);
        Assert.Equal(7, stored.LastCycleNumber);
        Assert.Equal(3, stored.LastReportedDeviceCount);
        Assert.Null(stored.HeartbeatMissedAt);
    }

    /// <summary>
    /// Idempotent by construction rather than by a dedupe row: a redelivered beat, or one that
    /// overtook its predecessor, leaves the stored heartbeat where it was.
    /// </summary>
    [Fact]
    public async Task Heartbeat_DeliveredTwice_MovesTheRecordOnlyForwards()
    {
        var poller = await RegisterAsync(NewPollerName());
        var beat = Heartbeat(poller.Name, DateTimeOffset.UtcNow, cycleNumber: 5);

        Assert.True(await RecordAsync(beat));
        Assert.False(await RecordAsync(beat));
        Assert.False(await RecordAsync(beat with
        {
            EventId = Guid.CreateVersion7(),
            OccurredAt = beat.OccurredAt.AddSeconds(-30),
            CycleNumber = 4,
        }));

        var stored = await ReadAsync(poller.Name);
        Assert.Equal(5, stored.LastCycleNumber);
    }

    /// <summary>
    /// The precision trap, pinned. A `DateTimeOffset` carries 100ns ticks and a `timestamptz` keeps
    /// microseconds, so a beat whose timestamp has sub-microsecond precision comes back from the
    /// database smaller than it went in — and a redelivery of that same beat looked newer than
    /// itself until the guard compared both sides at stored precision.
    /// </summary>
    [Fact]
    public async Task Heartbeat_WithSubMicrosecondPrecision_IsStillIgnoredOnRedelivery()
    {
        var poller = await RegisterAsync(NewPollerName());
        var awkward = new DateTimeOffset(
            DateTimeOffset.UtcNow.Ticks / 10 * 10 + 7, TimeSpan.Zero);
        var beat = Heartbeat(poller.Name, awkward, cycleNumber: 9);

        Assert.True(await RecordAsync(beat));
        Assert.False(await RecordAsync(beat));

        Assert.Equal(9, (await ReadAsync(poller.Name)).LastCycleNumber);
    }

    /// <summary>
    /// Registration is an authenticated statement about a poller's group; a message on a queue is
    /// not the place to make one.
    /// </summary>
    [Fact]
    public async Task Heartbeat_FromAPollerThatNeverRegistered_IsIgnored()
    {
        var name = NewPollerName();

        Assert.False(await RecordAsync(Heartbeat(name, DateTimeOffset.UtcNow)));

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        Assert.Null(await context.Pollers.SingleOrDefaultAsync(item => item.Name == name));
    }

    // ---- going quiet ----

    [Fact]
    public async Task Evaluate_PollerSilentForTwoIntervals_ReportsItOnceAndAuditsIt()
    {
        var poller = await RegisterAsync(NewPollerName());
        await RecordAsync(Heartbeat(poller.Name, DateTimeOffset.UtcNow.AddMinutes(-5)));

        Assert.Equal(1, await EvaluateAsync());

        var stored = await ReadAsync(poller.Name);
        Assert.NotNull(stored.HeartbeatMissedAt);

        await using var scope = _application.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Single(await platform.AuditEntries
            .Where(entry => entry.EntityType == "Poller"
                && entry.EntityId == stored.Id.ToString()
                && entry.Action == "HeartbeatMissed")
            .ToListAsync());
    }

    [Fact]
    public async Task Evaluate_RunTwice_ReportsAPollerOnlyOncePerSilence()
    {
        var poller = await RegisterAsync(NewPollerName());
        await RecordAsync(Heartbeat(poller.Name, DateTimeOffset.UtcNow.AddMinutes(-5)));

        await EvaluateAsync();
        var reported = await ReadAsync(poller.Name);
        await EvaluateAsync();
        await EvaluateAsync();

        var stored = await ReadAsync(poller.Name);
        Assert.Equal(reported.HeartbeatMissedAt, stored.HeartbeatMissedAt);

        await using var scope = _application.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Single(await platform.AuditEntries
            .Where(entry => entry.EntityId == stored.Id.ToString() && entry.Action == "HeartbeatMissed")
            .ToListAsync());
    }

    /// <summary>A poller that comes back gets a clean slate, so the next outage is reported too.</summary>
    [Fact]
    public async Task Evaluate_AfterAReportedPollerReturns_ReportsTheNextSilenceAgain()
    {
        var poller = await RegisterAsync(NewPollerName());
        await RecordAsync(Heartbeat(poller.Name, DateTimeOffset.UtcNow.AddMinutes(-5)));
        await EvaluateAsync();

        await RecordAsync(Heartbeat(poller.Name, DateTimeOffset.UtcNow, cycleNumber: 2));
        Assert.Null((await ReadAsync(poller.Name)).HeartbeatMissedAt);

        // The poller goes quiet a second time. A later beat cannot express that — the record only
        // moves forward — so the silence is made real by ageing the stored heartbeat.
        await BackdateHeartbeatAsync(poller.Name, DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.Equal(1, await EvaluateAsync());
        Assert.NotNull((await ReadAsync(poller.Name)).HeartbeatMissedAt);
    }

    private async Task BackdateHeartbeatAsync(string name, DateTimeOffset lastHeartbeatAt)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var poller = await context.Pollers.SingleAsync(item => item.Name == name);
        poller.LastHeartbeatAt = lastHeartbeatAt;
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Evaluate_PollerHeardFromRecently_ReportsNothing()
    {
        var poller = await RegisterAsync(NewPollerName());
        await RecordAsync(Heartbeat(poller.Name, DateTimeOffset.UtcNow));

        Assert.Equal(0, await EvaluateAsync());
        Assert.Null((await ReadAsync(poller.Name)).HeartbeatMissedAt);
    }

    // ---- fixtures ----

    private static string NewPollerName() => $"hb-{Guid.NewGuid():N}"[..20];

    private static PollerHeartbeat Heartbeat(
        string name,
        DateTimeOffset occurredAt,
        long cycleNumber = 1,
        int deviceCount = 0) => new(
            Guid.CreateVersion7(),
            occurredAt,
            name,
            "default",
            "0.1.0",
            ConfigVersion: 0,
            IntervalSeconds: 15,
            DeviceCount: deviceCount,
            CycleNumber: cycleNumber);

    private async Task<bool> RecordAsync(PollerHeartbeat heartbeat)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPollerHeartbeatService>();
        return await service.RecordAsync(heartbeat, CancellationToken.None);
    }

    private async Task<int> EvaluateAsync()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPollerHeartbeatService>();
        return await service.EvaluateAsync(CancellationToken.None);
    }

    private async Task<Poller> ReadAsync(string name)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        return await context.Pollers.AsNoTracking().SingleAsync(item => item.Name == name);
    }

    private async Task<PollerDto> RegisterAsync(string name)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/pollers/registrations", PollerRole);
        request.Content = JsonContent.Create(new { name, pollerGroup = "default", agentVersion = "0.1.0" });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<PollerDto>(await response.Content.ReadFromJsonAsync<PollerDto>());
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(HeartbeatAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record PollerDto(
        Guid Id,
        string Name,
        string PollerGroup,
        string? AgentVersion,
        DateTimeOffset? LastHeartbeatAt,
        int? HeartbeatIntervalSeconds,
        long LastCycleNumber,
        DateTimeOffset? HeartbeatMissedAt);

    private sealed class HeartbeatApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public HeartbeatApplication(
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
                    // The pass is driven by hand here, so the trigger interval is irrelevant; the
                    // threshold is the shipped default, because that is the number under test.
                    ["Monitoring:Heartbeat:MissedThreshold"] = "2",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = HeartbeatAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = HeartbeatAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = HeartbeatAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, HeartbeatAuthenticationHandler>(
                        HeartbeatAuthenticationHandler.TestScheme,
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

    private sealed class HeartbeatAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "HeartbeatTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "heartbeat-test-user-id"),
                    new Claim(ClaimTypes.Name, "heartbeat-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
