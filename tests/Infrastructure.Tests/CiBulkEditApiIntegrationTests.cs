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
using Platform.Data;
using Platform.Seeding;

namespace Infrastructure.Tests;

[Collection(InfrastructureCollection.Name)]
public sealed class CiBulkEditApiIntegrationTests : IAsyncLifetime
{
    private readonly BulkEditApplication _application;
    private HttpClient? _client;
    private DirectoryUserDto _owner = null!;
    private DirectorySiteDto _site = null!;

    public CiBulkEditApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new BulkEditApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await platformContext.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
        // Bulk assignment needs somebody to assign to, and the demo directory is where they live.
        await new DemoDataSeeder(platformContext).SeedAsync();

        _owner = (await GetAsync<List<DirectoryUserDto>>("/api/directory/users"))
            .Single(user => user.Username == "enduser1");
        _site = (await GetAsync<List<DirectorySiteDto>>("/api/directory/sites")).First();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BulkEdit_AssignsOwnerAndSiteToEverySelectedCi()
    {
        var cis = await Task.WhenAll(CreateLaptopAsync(), CreateLaptopAsync(), CreateLaptopAsync());

        var report = await BulkEditAsync(new
        {
            ciIds = cis.Select(ci => ci.Id),
            ownership = new { ownerUserId = _owner.Id, departmentId = (Guid?)null, siteId = _site.Id },
            note = "Handed out at induction",
        });

        Assert.Equal(3, report.Succeeded);
        Assert.Equal(0, report.Failed);
        foreach (var ci in cis)
        {
            var updated = await GetAsync<CiDto>($"/api/cis/{ci.Id}");
            Assert.Equal(_owner.Id, updated.Ownership.OwnerUserId);
            Assert.Equal(_site.Id, updated.Ownership.SiteId);
        }
    }

    [Fact]
    public async Task BulkEdit_MovesEverySelectedCiThroughTheLifecycleGraph()
    {
        var cis = await Task.WhenAll(CreateLaptopAsync(), CreateLaptopAsync());

        var report = await BulkEditAsync(new { ciIds = cis.Select(ci => ci.Id), lifecycleState = "Deployed" });

        Assert.Equal(2, report.Succeeded);
        foreach (var ci in cis)
        {
            var updated = await GetAsync<CiDto>($"/api/cis/{ci.Id}");
            Assert.Equal("Deployed", updated.LifecycleState);
            // The move has to be a real guarded transition, so it leaves history behind.
            var history = await GetAsync<List<HistoryDto>>($"/api/cis/{ci.Id}/lifecycle-transitions");
            Assert.Contains(history, entry => entry.FromState == "InStock" && entry.ToState == "Deployed");
        }
    }

    [Fact]
    public async Task BulkEdit_CiAlreadyInTheTargetState_CountsAsSucceeded()
    {
        var ci = await CreateLaptopAsync();
        await BulkEditAsync(new { ciIds = new[] { ci.Id }, lifecycleState = "Deployed" });

        var report = await BulkEditAsync(new { ciIds = new[] { ci.Id }, lifecycleState = "Deployed" });

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.Failed);
    }

    /// <summary>One refused CI must not take the rest of the selection down with it.</summary>
    [Fact]
    public async Task BulkEdit_IllegalTransitionForOneCi_ReportsItAndStillAppliesTheOthers()
    {
        var ordered = await CreateLaptopAsync(lifecycleState: "Ordered");
        var inStock = await CreateLaptopAsync();

        var report = await BulkEditAsync(new
        {
            ciIds = new[] { ordered.Id, inStock.Id },
            lifecycleState = "Deployed",
        });

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        var failed = Assert.Single(report.Rows, row => !row.Succeeded);
        Assert.Equal(ordered.Id, failed.CiId);
        Assert.Contains("cannot move from Ordered to Deployed", failed.Error);
        Assert.Equal("Deployed", (await GetAsync<CiDto>($"/api/cis/{inStock.Id}")).LifecycleState);
        Assert.Equal("Ordered", (await GetAsync<CiDto>($"/api/cis/{ordered.Id}")).LifecycleState);
    }

    [Fact]
    public async Task BulkEdit_UnknownCi_IsReportedWithoutFailingTheRequest()
    {
        var ci = await CreateLaptopAsync();

        var report = await BulkEditAsync(new
        {
            ciIds = new[] { ci.Id, Guid.CreateVersion7() },
            lifecycleState = "Deployed",
        });

        Assert.Equal(1, report.Succeeded);
        Assert.Contains(report.Rows, row => !row.Succeeded && row.Error == "The CI no longer exists.");
    }

    [Fact]
    public async Task BulkEdit_WithNothingToChange_ReturnsValidationProblem()
    {
        var ci = await CreateLaptopAsync();

        using var request = Authenticated(HttpMethod.Post, "/api/cis/bulk-edit");
        request.Content = JsonContent.Create(new { ciIds = new[] { ci.Id } });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("ownership change, a lifecycle state, or both", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkEdit_WithNoSelection_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis/bulk-edit");
        request.Content = JsonContent.Create(new { ciIds = Array.Empty<Guid>(), lifecycleState = "Deployed" });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkEdit_AsEndUser_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis/bulk-edit", "EndUser");
        request.Content = JsonContent.Create(new { ciIds = new[] { Guid.CreateVersion7() }, lifecycleState = "Deployed" });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<ReportDto> BulkEditAsync(object body)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis/bulk-edit");
        request.Content = JsonContent.Create(body);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ReportDto>(await response.Content.ReadFromJsonAsync<ReportDto>());
    }

    private async Task<CiDto> CreateLaptopAsync(string lifecycleState = "InStock")
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "Hardware",
            name = $"Bulk laptop {Guid.NewGuid():N}",
            lifecycleState,
            attributes = new Dictionary<string, string> { ["manufacturer"] = "Dell", ["model"] = "5550" },
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
        request.Headers.Add(BulkEditAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record ReportDto(int Total, int Succeeded, int Failed, List<ReportRowDto> Rows);

    private sealed record ReportRowDto(Guid CiId, string? Name, bool Succeeded, string? Error);

    private sealed record CiDto(Guid Id, string Name, string LifecycleState, OwnershipDto Ownership);

    private sealed record OwnershipDto(Guid? OwnerUserId, string? OwnerName, Guid? DepartmentId, Guid? SiteId);

    private sealed record HistoryDto(Guid Id, Guid CiId, string FromState, string ToState);

    private sealed record DirectoryUserDto(Guid Id, string Username, string DisplayName);

    private sealed record DirectorySiteDto(Guid Id, string Name);

    private sealed class BulkEditApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public BulkEditApplication(
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
                        options.DefaultAuthenticateScheme = BulkEditAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = BulkEditAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = BulkEditAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, BulkEditAuthenticationHandler>(
                        BulkEditAuthenticationHandler.TestScheme,
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

    private sealed class BulkEditAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "BulkEditTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "bulk-edit-test-user-id"),
                    new Claim(ClaimTypes.Name, "bulk-edit-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
