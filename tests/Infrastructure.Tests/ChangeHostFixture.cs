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
/// The host the WP-5.8 change suite uses, built once per class.
/// <para>
/// A class fixture rather than work done in the test class's own <c>InitializeAsync</c>, following
/// WP-5.4, WP-5.5 and WP-5.7: xUnit constructs a test class once <em>per test</em>, so a host built
/// there is a host built per test — and every host in this suite calls <c>RemoveAll&lt;IHostedService&gt;()</c>,
/// so each one leaves outbox rows nothing will ever deliver.
/// </para>
/// <para>
/// Its authentication handler carries an actor id as well as a role, which no earlier fixture needed:
/// WP-5.8 refuses to let anybody approve their own change, so proving that takes two people rather than
/// two roles.
/// </para>
/// </summary>
public sealed class ChangeHostFixture : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChangeApplication? _application;
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

            _application = new ChangeApplication(
                infrastructure.PostgresConnectionString,
                infrastructure.RabbitMqConnectionString,
                infrastructure.MinioConnectionString);
            Client = _application.CreateClient();

            await using (var scope = _application.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
                // Helpdesk too: deleting a CI asks the ticket-link port whether anything still names it.
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

    public HttpRequestMessage Request(
        HttpMethod method,
        string uri,
        string role = "Technician",
        string actorId = ChangeAuthenticationHandler.DefaultActorId)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(ChangeAuthenticationHandler.RoleHeader, role);
        request.Headers.Add(ChangeAuthenticationHandler.ActorHeader, actorId);
        return request;
    }

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
        string role = "Technician",
        string actorId = ChangeAuthenticationHandler.DefaultActorId)
    {
        var request = Request(method, uri, role, actorId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Client.SendAsync(request);
    }

    private sealed class ChangeApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public ChangeApplication(
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
                    // The bus is on so approvals reach the outbox, which is where this suite reads them
                    // from; nothing delivers them, which is what keeps the assertions deterministic.
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ChangeAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = ChangeAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = ChangeAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ChangeAuthenticationHandler>(
                        ChangeAuthenticationHandler.TestScheme,
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

    internal sealed class ChangeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "ChangeTest";
        public const string RoleHeader = "X-Test-Role";
        public const string ActorHeader = "X-Test-Actor";
        public const string DefaultActorId = "change-test-requester";
        public const string OtherActorId = "change-test-approver";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var actorId = Request.Headers[ActorHeader].ToString();
            if (string.IsNullOrWhiteSpace(actorId))
            {
                actorId = DefaultActorId;
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, actorId),
                    new Claim("sub", actorId),
                    new Claim("name", actorId),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
