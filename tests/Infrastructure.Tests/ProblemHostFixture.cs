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

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The host the two WP-5.7 suites share, built once per class.
/// <para>
/// A class fixture rather than work done in the test class's own <c>InitializeAsync</c>, following WP-5.4
/// and WP-5.5: xUnit constructs a test class once <em>per test</em>, so a host built there is a host built
/// per test — and every host in this suite calls <c>RemoveAll&lt;IHostedService&gt;()</c>, so each one
/// leaves outbox rows nothing will ever deliver.
/// </para>
/// <para>
/// The detection threshold is pinned to a number of its own rather than left at the default, because the
/// suite shares a database with forty other classes and the seeded backlog: at the default of five, a pass
/// would count whatever the neighbours happened to have created. Every test here works on a CI id and a
/// category it made itself.
/// </para>
/// </summary>
public sealed class ProblemHostFixture : IDisposable
{
    /// <summary>
    /// The threshold this suite runs at. Three rather than the default five so a test can seed one below
    /// and one above it cheaply, and so the number under test is stated rather than inherited.
    /// </summary>
    public const int MinimumIncidents = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProblemApplication? _application;
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

            _application = new ProblemApplication(
                infrastructure.PostgresConnectionString,
                infrastructure.RabbitMqConnectionString,
                infrastructure.MinioConnectionString);
            Client = _application.CreateClient();

            await using (var scope = _application.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
                // Assets too: a problem's subject name is read through ICiDirectory, which is Assets'.
                await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
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

    internal static HttpRequestMessage Authenticate(HttpRequestMessage request, string role = "Technician")
    {
        request.Headers.Add(ProblemAuthenticationHandler.RoleHeader, role);
        return request;
    }

    public HttpRequestMessage Request(HttpMethod method, string uri, string role = "Technician") =>
        Authenticate(new HttpRequestMessage(method, uri), role);

    public async Task<T> GetAsync<T>(string uri, string role = "Technician")
    {
        using var request = Request(HttpMethod.Get, uri, role);
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    public Task<HttpResponseMessage> PostAsync(string uri, object? body, string role = "Technician")
    {
        var request = Request(HttpMethod.Post, uri, role);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Client.SendAsync(request);
    }

    private sealed class ProblemApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public ProblemApplication(
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
                    // The detector's own job stays off; every pass in these tests is asked for explicitly,
                    // so nothing races the assertions.
                    ["Platform:EnableScheduler"] = "false",
                    ["Helpdesk:ProblemDetection:MinimumIncidents"] = MinimumIncidents.ToString(),
                    ["Helpdesk:ProblemDetection:WindowDays"] = "7",
                    ["Helpdesk:ProblemDetection:DismissalCooldownDays"] = "7",
                    ["Helpdesk:ProblemDetection:MaxSuggestionsPerRun"] = "500",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ProblemAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = ProblemAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = ProblemAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ProblemAuthenticationHandler>(
                        ProblemAuthenticationHandler.TestScheme,
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

    internal sealed class ProblemAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "ProblemTest";
        public const string RoleHeader = "X-Test-Role";
        public const string ActorId = "problem-test-user-id";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, ActorId),
                    new Claim("sub", ActorId),
                    new Claim("name", "Problem Test"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
