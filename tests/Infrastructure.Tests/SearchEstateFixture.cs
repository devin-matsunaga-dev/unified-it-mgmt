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
using Modules.Assets.Data;
using Modules.Helpdesk.Data;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The host and the records <see cref="SearchApiIntegrationTests"/> searches, built once for the whole
/// class.
/// <para>
/// A class fixture rather than work done in the test class's own <c>InitializeAsync</c>, because xUnit
/// constructs a test class once <em>per test</em>: an estate built there would be built twenty times over.
/// That matters twice here. It is the outbox-volume problem this suite already carries a standing note
/// about — every host in it removes MassTransit's delivery service, so each CI and ticket written leaves a
/// row nothing will ever deliver — and, worse for the assertions, twenty estates all carrying the same
/// marker would make every count depend on how many tests had run before it.
/// </para>
/// <para>
/// The connection strings arrive through <see cref="EnsureInitialisedAsync"/> rather than the constructor
/// because xUnit cannot hand a collection fixture to a class fixture. The first test through does the work
/// and the rest wait on the same task.
/// </para>
/// </summary>
public sealed class SearchEstateFixture : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SearchApplication? _application;
    private Estate? _records;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _application!.Services;

    public Estate Records => _records!;

    public async Task EnsureInitialisedAsync(InfrastructureFixture infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        if (_records is not null)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_records is not null)
            {
                return;
            }

            _application = new SearchApplication(
                infrastructure.PostgresConnectionString,
                infrastructure.RabbitMqConnectionString,
                infrastructure.MinioConnectionString);
            Client = _application.CreateClient();

            await using (var scope = _application.Services.CreateAsyncScope())
            {
                // All four, because a search reads all four. Unlike the port trap this is not a query
                // mentioning one module failing because of another's schema — each source fails on its own
                // — but the remedy is the same one nine packages have now needed.
                await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
            }

            _records = await BuildAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _application?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// One record of every kind carrying the marker, plus the three that make ranking, capping and
    /// visibility assertable: a second CI that only mentions the word, a cleared alert, and a ticket
    /// belonging to somebody else.
    /// <para>
    /// Written mostly through the endpoints that own each record, so the generated tsvectors are produced by
    /// the same path production takes. The device, the alerts and the user profile go straight to their
    /// tables — an alert is raised by a poller over three cycles and a user profile has no write endpoint —
    /// and what is under test is the read.
    /// </para>
    /// </summary>
    private async Task<Estate> BuildAsync()
    {
        const string marker = SearchApiIntegrationTests.Marker;
        var requesterId = $"search-tests-{Guid.NewGuid():N}";
        var requesterName = $"Marion {marker}stead";
        var assetTag = $"ZQ-{Random.Shared.Next(1000, 9999)}-{marker[..4].ToUpperInvariant()}";
        var serialNumber = $"{marker.ToUpperInvariant()}{Random.Shared.Next(100000, 999999)}";
        var address = $"10.77.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(2, 250)}";

        // The CI whose *name* carries the marker, and a second whose description only mentions it. The pair
        // is what makes the weighting assertable — without the second one, "ranked first" is vacuous.
        var ciId = await CreateCiAsync($"{marker} core switch", assetTag, serialNumber, "Top of rack.");
        var mentionedCiId = await CreateCiAsync(
            $"Peer switch {Guid.NewGuid():N}"[..40],
            null,
            null,
            $"Cross-connected to the {marker} core switch in the same rack.");

        var (ticketId, ticketNumber) = await CreateTicketAsync(
            $"{marker} switch is unreachable", requesterId, requesterName);
        var (otherTicketId, _) = await CreateTicketAsync(
            $"{marker} switch is unreachable", $"search-tests-other-{Guid.NewGuid():N}", "Sam Elsewhere");

        Guid userId;
        await using (var scope = _application!.Services.CreateAsyncScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // A user profile needs a site and a department as required foreign keys, and this suite's
            // database is not seeded — so this fixture writes its own rather than borrowing whichever rows
            // another class happened to leave behind. The codes come from the *tail* of a v7 GUID: v7 leads
            // with a timestamp, so ids minted in one run share their leading hex and collide on the unique
            // code index — the trap WP-4.6's audit suite found by failing as a group and passing alone.
            var suffix = $"{Guid.CreateVersion7():N}"[^6..].ToUpperInvariant();
            var site = new Site { Id = Guid.CreateVersion7(), Code = $"SR{suffix}", Name = $"Search site {suffix}" };
            var department = new Department
            {
                Id = Guid.CreateVersion7(), Code = $"SD{suffix}", Name = $"Search team {suffix}",
            };
            platform.Sites.Add(site);
            platform.Departments.Add(department);

            userId = Guid.CreateVersion7();
            platform.UserProfiles.Add(new UserProfile
            {
                Id = userId,
                Username = requesterId,
                Email = $"{requesterId}@example.test",
                DisplayName = requesterName,
                Role = "EndUser",
                SiteId = site.Id,
                DepartmentId = department.Id,
            });
            await platform.SaveChangesAsync();
        }

        Guid deviceId;
        Guid alertId;
        Guid clearedAlertId;
        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var monitoring = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            deviceId = Guid.CreateVersion7();
            monitoring.MonitoredDevices.Add(new MonitoredDevice
            {
                Id = deviceId,
                CiId = ciId,
                Address = address,
                PollerGroup = "default",
                Notes = $"The {marker} core switch's management interface.",
                CreatedBy = "search-tests",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "search-tests",
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            alertId = Guid.CreateVersion7();
            clearedAlertId = Guid.CreateVersion7();
            monitoring.Alerts.AddRange(
                new Alert
                {
                    Id = alertId,
                    DeviceId = deviceId,
                    CiId = ciId,
                    CheckId = Guid.CreateVersion7(),
                    RuleId = $"check:{marker}:cpu",
                    MetricName = "cpu.percent",
                    Severity = AlertSeverity.Critical,
                    Status = AlertStatus.Open,
                    Summary = $"{marker} CPU above 90%",
                    RaisedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    LastObservedAt = DateTimeOffset.UtcNow,
                    PollerName = "search-tests",
                },
                new Alert
                {
                    Id = clearedAlertId,
                    DeviceId = deviceId,
                    CiId = ciId,
                    CheckId = Guid.CreateVersion7(),
                    RuleId = $"check:{marker}:availability",
                    MetricName = "check.success",
                    Severity = AlertSeverity.Warning,
                    Status = AlertStatus.Cleared,
                    Summary = $"{marker} did not answer ICMP",
                    RaisedAt = DateTimeOffset.UtcNow.AddDays(-2),
                    LastObservedAt = DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(10),
                    ClearedAt = DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(10),
                    PollerName = "search-tests",
                });
            await monitoring.SaveChangesAsync();
        }

        return new Estate(
            ciId, mentionedCiId, CiCount: 2, assetTag, serialNumber,
            ticketId, ticketNumber, otherTicketId, TicketCount: 2, requesterId, requesterName,
            userId, deviceId, address, alertId, clearedAlertId);
    }

    private async Task<Guid> CreateCiAsync(string name, string? assetTag, string? serial, string description)
    {
        using var request = Authenticate(new HttpRequestMessage(HttpMethod.Post, "/api/cis"));
        request.Content = JsonContent.Create(new
        {
            type = "NetworkDevice",
            name,
            assetTag,
            serialNumber = serial,
            description,
            // Left at the default: a CI is registered on order or in the store room, and every later state
            // has to be reached through a guarded transition (WP-2.2).
            attributes = new Dictionary<string, string>
            {
                ["managementIp"] = $"10.77.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(2, 250)}",
                ["vendor"] = "Cisco",
                ["portCount"] = "48",
            },
        });
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>()).Id;
    }

    /// <summary>
    /// Raised as the requester rather than on their behalf, because the visibility test needs a subject it
    /// can then search as — and an agent raising a ticket for somebody else records the agent's own id.
    /// </summary>
    private async Task<(Guid Id, string Number)> CreateTicketAsync(
        string title,
        string requesterId,
        string requesterName)
    {
        using var request = Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/tickets"), "EndUser", requesterId, requesterName);
        request.Content = JsonContent.Create(new
        {
            title,
            description = "Raised by the global search integration test.",
            type = "Incident",
            urgency = "Medium",
            impact = "Medium",
        });
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = Assert.IsType<TicketDto>(await response.Content.ReadFromJsonAsync<TicketDto>());
        return (ticket.Id, ticket.Number);
    }

    internal static HttpRequestMessage Authenticate(
        HttpRequestMessage request,
        string role = "Technician",
        string? subject = null,
        string? name = null)
    {
        request.Headers.Add(SearchAuthenticationHandler.RoleHeader, role);
        request.Headers.Add(SearchAuthenticationHandler.SubjectHeader, subject ?? "search-test-subject");
        request.Headers.Add(SearchAuthenticationHandler.NameHeader, name ?? "Search Test");
        return request;
    }

    private sealed record CiDto(Guid Id, string Name);

    private sealed record TicketDto(Guid Id, string Number, string Title);

    private sealed class SearchApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public SearchApplication(
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
                    // Creating a CI and raising a ticket both publish through the outbox, so the bus has to
                    // be configured even though nothing here reads a message.
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = SearchAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = SearchAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = SearchAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, SearchAuthenticationHandler>(
                        SearchAuthenticationHandler.TestScheme,
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

    /// <summary>
    /// Carries the subject as well as the role, unlike every test handler before it. This package's
    /// visibility rule is "whose records are these", so a fixed subject would make the end-user test assert
    /// nothing at all.
    /// </summary>
    internal sealed class SearchAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "SearchTest";
        public const string RoleHeader = "X-Test-Role";
        public const string SubjectHeader = "X-Test-Subject";
        public const string NameHeader = "X-Test-Name";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var subject = Request.Headers.TryGetValue(SubjectHeader, out var value)
                ? value.ToString()
                : "search-test-subject";
            var name = Request.Headers.TryGetValue(NameHeader, out var displayName)
                ? displayName.ToString()
                : "Search Test";

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, subject),
                    new Claim("sub", subject),
                    new Claim("name", name),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}

/// <param name="CiCount">
/// How many CIs carry the marker. Exact because the estate is built once — the assertion that the total
/// survives a cap depends on knowing the real number.
/// </param>
/// <param name="TicketCount">Tickets carrying the marker across both requesters.</param>
public sealed record Estate(
    Guid CiId,
    Guid CiMentionedInDescriptionId,
    int CiCount,
    string AssetTag,
    string SerialNumber,
    Guid TicketId,
    string TicketNumber,
    Guid OtherRequestersTicketId,
    int TicketCount,
    string RequesterId,
    string RequesterName,
    Guid UserId,
    Guid DeviceId,
    string Address,
    Guid AlertId,
    Guid ClearedAlertId);
