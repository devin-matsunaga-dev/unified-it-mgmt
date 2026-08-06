using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Host.Authentication;

namespace Infrastructure.Tests;

public sealed class AuthenticationEndpointTests : IClassFixture<AuthenticationEndpointTests.AuthenticatedApplication>
{
    private readonly HttpClient _client;

    public AuthenticationEndpointTests(AuthenticatedApplication application)
    {
        _client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Technician")]
    [InlineData("Manager")]
    [InlineData("EndUser")]
    public async Task Me_AuthenticatedRole_ReturnsIdentityAndRole(string role)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add(TestAuthenticationHandler.RoleHeader, role);

        using var response = await _client.SendAsync(request);
        var currentUser = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(currentUser);
        Assert.Equal("test-user-id", currentUser.Id);
        Assert.Equal("test-user", currentUser.Name);
        Assert.Contains(role, currentUser.Roles);
    }

    [Fact]
    public async Task AdminAccessCheck_EndUser_ReturnsForbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/access-check");
        request.Headers.Add(TestAuthenticationHandler.RoleHeader, "EndUser");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminAccessCheck_Admin_ReturnsNoContent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/access-check");
        request.Headers.Add(TestAuthenticationHandler.RoleHeader, "Admin");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Me_Anonymous_ReturnsUnauthorized()
    {
        using var response = await _client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuditTest_MissingValue_ReturnsValidationProblem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dev/audit-test");
        request.Headers.Add(TestAuthenticationHandler.RoleHeader, "Admin");
        request.Content = JsonContent.Create(new { id = Guid.CreateVersion7(), value = "" });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SystemPing_MissingDedupeKey_ReturnsValidationProblem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dev/ping");
        request.Headers.Add(TestAuthenticationHandler.RoleHeader, "Admin");
        request.Content = JsonContent.Create(new { dedupeKey = "" });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SystemPing_EndUser_ReturnsForbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dev/ping");
        request.Headers.Add(TestAuthenticationHandler.RoleHeader, "EndUser");
        request.Content = JsonContent.Create(new { dedupeKey = "forbidden-ping" });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Logout_Requested_RedirectsToConfiguredIdentityProvider()
    {
        using var response = await _client.GetAsync("/api/auth/logout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "https://identity.example.test/realms/it-platform/protocol/openid-connect/logout" +
            "?client_id=it-platform-web&post_logout_redirect_uri=https%3A%2F%2Fapp.example.test%2F",
            response.Headers.Location?.AbsoluteUri);
    }

    public sealed class AuthenticatedApplication : WebApplicationFactory<Program>
    {
        public AuthenticatedApplication()
        {
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__database",
                "Host=localhost;Database=unused;Username=unused;Password=unused");
            Environment.SetEnvironmentVariable("Platform__EnableMessageBus", "false");
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
                    ["Authentication:RequireHttpsMetadata"] = "true",
                    ["ConnectionStrings:database"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
                    ["Platform:ApplyMigrations"] = "false",
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.TestScheme;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.TestScheme;
                    options.DefaultForbidScheme = TestAuthenticationHandler.TestScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.TestScheme,
                    _ => { }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("Platform__EnableMessageBus", null);
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "Test";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim("sub", "test-user-id"),
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Email, "test-user@example.test"),
                new Claim(ClaimTypes.Role, role.ToString()),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
