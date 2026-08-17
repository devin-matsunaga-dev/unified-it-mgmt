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
/// The host <see cref="DashboardApiIntegrationTests"/> reads, built once for the whole class.
/// <para>
/// A class fixture rather than work done in the test class's own <c>InitializeAsync</c>, following WP-5.4:
/// xUnit constructs a test class once <em>per test</em>, so a host built there would be built once per test
/// as well. That matters here for the reason this suite carries a standing note about — every host in it
/// calls <c>RemoveAll&lt;IHostedService&gt;()</c>, which removes MassTransit's outbox delivery service, so
/// everything a test writes leaves an outbox row nothing will ever deliver.
/// </para>
/// <para>
/// Unlike the search fixture there is no estate to build. Every widget here reads the whole estate over a
/// database the entire suite shares, so no count could ever be exact; the tests assert on structure, on
/// deltas they cause themselves, and — for the layouts, which are per person — on a subject of their own.
/// </para>
/// </summary>
public sealed class DashboardHostFixture : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DashboardApplication? _application;
    private bool _initialised;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _application!.Services;

    public async Task EnsureInitialisedAsync(InfrastructureFixture infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        if (_initialised)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_initialised)
            {
                return;
            }

            _application = new DashboardApplication(
                infrastructure.PostgresConnectionString,
                infrastructure.RabbitMqConnectionString,
                infrastructure.MinioConnectionString);
            Client = _application.CreateClient();

            await using (var scope = _application.Services.CreateAsyncScope())
            {
                // All four, because one dashboard read reaches all four schemas. This is the tenth package
                // to need this: an unmigrated schema behind any one widget answers 42P01, and — unlike
                // every read before WP-5.5 — it would come back as a card marked Failed rather than as a
                // 500, which is a far quieter way to lose a test.
                await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
            }

            _initialised = true;
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
    /// Every request carries a role and a subject. The subject matters more here than in any suite before
    /// it: a saved layout belongs to one person, so a test that shared a subject with its neighbours would
    /// be asserting against whatever the last one arranged.
    /// </summary>
    internal static HttpRequestMessage Authenticate(
        HttpRequestMessage request,
        string role = "Technician",
        string? subject = null)
    {
        request.Headers.Add(DashboardAuthenticationHandler.RoleHeader, role);
        request.Headers.Add(DashboardAuthenticationHandler.SubjectHeader, subject ?? "dashboard-test-subject");
        return request;
    }

    private sealed class DashboardApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public DashboardApplication(
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
                    // Raising a ticket publishes through the outbox, so the bus has to be configured even
                    // though nothing here reads a message.
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = DashboardAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = DashboardAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = DashboardAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, DashboardAuthenticationHandler>(
                        DashboardAuthenticationHandler.TestScheme,
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

    internal sealed class DashboardAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "DashboardTest";
        public const string RoleHeader = "X-Test-Role";
        public const string SubjectHeader = "X-Test-Subject";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var subject = Request.Headers.TryGetValue(SubjectHeader, out var value)
                ? value.ToString()
                : "dashboard-test-subject";

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, subject),
                    new Claim("sub", subject),
                    new Claim("name", "Dashboard Test"),
                    // Split on commas so a test can be an admin *and* a manager, which is the one
                    // combination the preset rule has an opinion about.
                    .. role.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries)
                        .Select(item => new Claim(ClaimTypes.Role, item)),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
