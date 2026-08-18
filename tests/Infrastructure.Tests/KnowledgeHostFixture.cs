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

using Modules.Helpdesk.Data;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The host WP-5.9's suite shares, built once per class.
/// <para>
/// A class fixture rather than work done in the test class's own <c>InitializeAsync</c>, following WP-5.4,
/// WP-5.5 and WP-5.7: xUnit constructs a test class once <em>per test</em>, so a host built there is a host
/// built per test — and every host in this suite calls <c>RemoveAll&lt;IHostedService&gt;()</c>, so each one
/// leaves outbox rows nothing will ever deliver.
/// </para>
/// <para>
/// Like the search suite and for the same reason, everything this suite writes carries a nonsense marker
/// token. A knowledge search is rooted at a <em>word</em> over a database forty other classes share, so an
/// assertion about "the article that matched" has to be about a word only this class has ever written.
/// </para>
/// </summary>
public sealed class KnowledgeHostFixture : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KnowledgeApplication? _application;
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

            _application = new KnowledgeApplication(
                infrastructure.PostgresConnectionString,
                infrastructure.RabbitMqConnectionString,
                infrastructure.MinioConnectionString);
            Client = _application.CreateClient();

            await using (var scope = _application.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
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

    public HttpRequestMessage Request(HttpMethod method, string uri, string role = "Technician") =>
        Authenticate(new HttpRequestMessage(method, uri), role);

    public async Task<T> GetAsync<T>(string uri, string role = "Technician")
    {
        using var request = Request(HttpMethod.Get, uri, role);
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string uri,
        object? body = null,
        string role = "Technician")
    {
        var request = Request(method, uri, role);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Client.SendAsync(request);
    }

    internal static HttpRequestMessage Authenticate(HttpRequestMessage request, string role = "Technician")
    {
        request.Headers.Add(KnowledgeAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed class KnowledgeApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public KnowledgeApplication(
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
                        options.DefaultAuthenticateScheme = KnowledgeAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = KnowledgeAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = KnowledgeAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, KnowledgeAuthenticationHandler>(
                        KnowledgeAuthenticationHandler.TestScheme,
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

    internal sealed class KnowledgeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "KnowledgeTest";
        public const string RoleHeader = "X-Test-Role";
        public const string ActorId = "knowledge-test-user-id";

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
                    new Claim("name", "Knowledge Test"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
