using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

using MassTransit.EntityFrameworkCoreIntegration;
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
public sealed class CiLifecycleApiIntegrationTests : IAsyncLifetime
{
    private readonly LifecycleApplication _application;
    private HttpClient? _client;
    private DirectoryUserDto _owner = null!;
    private DirectoryUserDto _otherOwner = null!;

    public CiLifecycleApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new LifecycleApplication(
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
        // The WP verifies assignment against a seeded user, so the demo directory has to be present.
        await new DemoDataSeeder(platformContext).SeedAsync();

        var users = await GetAsync<List<DirectoryUserDto>>("/api/directory/users");
        _owner = users.Single(user => user.Username == "enduser1");
        _otherOwner = users.Single(user => user.Username == "enduser2");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The WP's first verification step: the illegal jump must be refused.</summary>
    [Fact]
    public async Task Transition_OrderedToDisposed_ReturnsConflict()
    {
        var ci = await CreateLaptopAsync(lifecycleState: "Ordered");

        using var response = await TransitionAsync(ci.Id, "Disposed");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("cannot move from Ordered to Disposed", problem, StringComparison.Ordinal);

        var unchanged = await GetAsync<CiDto>($"/api/cis/{ci.Id}");
        Assert.Equal("Ordered", unchanged.LifecycleState);
        Assert.Empty(await GetAsync<List<LifecycleHistoryDto>>($"/api/cis/{ci.Id}/lifecycle-transitions"));
    }

    [Fact]
    public async Task Transition_AlongTheChain_RecordsCompleteHistoryAndAuditsEachStep()
    {
        var ci = await CreateLaptopAsync(lifecycleState: "Ordered");

        foreach (var state in (string[])["InStock", "Deployed", "InRepair", "Deployed", "Retired", "Disposed"])
        {
            using var response = await TransitionAsync(ci.Id, state, $"Moved to {state}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var history = await GetAsync<List<LifecycleHistoryDto>>($"/api/cis/{ci.Id}/lifecycle-transitions");
        Assert.Equal(
            ["Ordered", "InStock", "Deployed", "InRepair", "Deployed", "Retired"],
            history.Select(entry => entry.FromState));
        Assert.Equal(
            ["InStock", "Deployed", "InRepair", "Deployed", "Retired", "Disposed"],
            history.Select(entry => entry.ToState));
        Assert.All(history, entry => Assert.Equal("ci-lifecycle-test-user-id", entry.ActorId));

        // Disposal closes the record: it drops out of the active list and is frozen against edits.
        var disposed = await GetAsync<CiDto>($"/api/cis/{ci.Id}");
        Assert.Equal("Disposed", disposed.LifecycleState);
        Assert.False(disposed.IsActive);

        await using var scope = _application.Services.CreateAsyncScope();
        var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audits = await platformContext.AuditEntries
            .Where(entry => entry.EntityType == "Ci" && entry.EntityId == ci.Id.ToString())
            .ToListAsync();

        Assert.Equal(6, audits.Count(entry => entry.Action == "LifecycleChanged"));
        Assert.Contains(
            await platformContext.Set<OutboxMessage>().ToListAsync(),
            message => message.MessageType.Contains(
                nameof(Contracts.Events.CiLifecycleChanged), StringComparison.Ordinal));
    }

    /// <summary>The WP's second verification step: the laptop shows up under the seeded user.</summary>
    [Fact]
    public async Task Assign_ToSeededUser_ChecksOutAndAppearsOnThatUsersAssets()
    {
        var ci = await CreateLaptopAsync();
        await TransitionAsync(ci.Id, "Deployed");

        var assigned = await AssignAsync(ci.Id, _owner.Id, _owner.DepartmentId, _owner.SiteId, "Onboarding");

        Assert.Equal(_owner.Id, assigned.Ownership.OwnerUserId);
        Assert.Equal(_owner.DisplayName, assigned.Ownership.OwnerName);
        Assert.Equal(_owner.SiteName, assigned.Ownership.SiteName);
        Assert.NotNull(assigned.Ownership.AssignedAt);

        var mine = await GetAsync<CiPageDto>($"/api/cis?ownerUserId={_owner.Id}");
        Assert.Contains(mine.Items, item => item.Id == ci.Id);

        var theirs = await GetAsync<CiPageDto>($"/api/cis?ownerUserId={_otherOwner.Id}");
        Assert.DoesNotContain(theirs.Items, item => item.Id == ci.Id);

        var log = await GetAsync<List<AssignmentDto>>($"/api/cis/{ci.Id}/assignments");
        var entry = Assert.Single(log);
        Assert.Equal("CheckOut", entry.Action);
        Assert.Null(entry.FromOwnerUserId);
        Assert.Equal(_owner.Id, entry.ToOwnerUserId);
        Assert.Equal("Onboarding", entry.Note);
    }

    [Fact]
    public async Task Assign_TransferThenCheckIn_LogsEveryHandoverInOrder()
    {
        var ci = await CreateLaptopAsync();
        await AssignAsync(ci.Id, _owner.Id, _owner.DepartmentId, _owner.SiteId);
        await AssignAsync(ci.Id, _otherOwner.Id, _otherOwner.DepartmentId, _otherOwner.SiteId);
        var checkedIn = await AssignAsync(ci.Id, ownerUserId: null, departmentId: null, siteId: null, note: "Returned to stores");

        Assert.Null(checkedIn.Ownership.OwnerUserId);
        Assert.Null(checkedIn.Ownership.AssignedAt);

        var log = await GetAsync<List<AssignmentDto>>($"/api/cis/{ci.Id}/assignments");
        Assert.Equal(["CheckOut", "Transfer", "CheckIn"], log.Select(entry => entry.Action));
        Assert.Equal(_owner.Id, log[1].FromOwnerUserId);
        Assert.Equal(_otherOwner.Id, log[1].ToOwnerUserId);
        Assert.Null(log[2].ToOwnerUserId);
    }

    [Fact]
    public async Task Transition_ToRetired_ChecksTheCiBackIn()
    {
        var ci = await CreateLaptopAsync();
        await TransitionAsync(ci.Id, "Deployed");
        await AssignAsync(ci.Id, _owner.Id, _owner.DepartmentId, _owner.SiteId);

        using var response = await TransitionAsync(ci.Id, "Retired", "End of life");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retired = await GetAsync<CiDto>($"/api/cis/{ci.Id}");
        Assert.Null(retired.Ownership.OwnerUserId);

        var log = await GetAsync<List<AssignmentDto>>($"/api/cis/{ci.Id}/assignments");
        Assert.Equal(["CheckOut", "CheckIn"], log.Select(entry => entry.Action));
        Assert.Equal(_owner.Id, log[1].FromOwnerUserId);
    }

    [Fact]
    public async Task Assign_UnknownUser_ReturnsValidationProblem()
    {
        var ci = await CreateLaptopAsync();

        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ci.Id}/assignment");
        request.Content = JsonContent.Create(new { ownerUserId = Guid.CreateVersion7() });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
        Assert.Empty(await GetAsync<List<AssignmentDto>>($"/api/cis/{ci.Id}/assignments"));
    }

    [Fact]
    public async Task Assign_WhenDisposed_ReturnsConflict()
    {
        var ci = await CreateLaptopAsync();
        await TransitionAsync(ci.Id, "Retired");
        await TransitionAsync(ci.Id, "Disposed");

        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ci.Id}/assignment");
        request.Content = JsonContent.Create(new { ownerUserId = _owner.Id });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateCi_WhenDisposed_ReturnsConflict()
    {
        var ci = await CreateLaptopAsync();
        await TransitionAsync(ci.Id, "Retired");
        await TransitionAsync(ci.Id, "Disposed");

        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ci.Id}");
        request.Content = JsonContent.Create(new
        {
            name = "Renamed after disposal",
            isActive = true,
            attributes = new Dictionary<string, string> { ["manufacturer"] = "Dell", ["model"] = "5550" },
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("disposed CI can no longer be edited", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCi_StartingPastInStock_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "Hardware",
            name = "Laptop that skipped the store room",
            lifecycleState = "Deployed",
            attributes = new Dictionary<string, string> { ["manufacturer"] = "Dell", ["model"] = "5550" },
        });

        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("starts as Ordered or InStock", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLifecycleStates_ReturnsTheGuardAsData()
    {
        var states = await GetAsync<List<LifecycleStateDto>>("/api/ci-lifecycle-states");

        Assert.Equal(6, states.Count);
        Assert.Equal(["Deployed", "InRepair", "Retired"], states.Single(state => state.State == "InStock").AllowedTargets);
        Assert.Empty(states.Single(state => state.State == "Disposed").AllowedTargets);
    }

    [Fact]
    public async Task Transition_UnknownCi_ReturnsNotFoundProblem()
    {
        using var response = await TransitionAsync(Guid.CreateVersion7(), "Deployed");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Assign_AsEndUser_IsForbidden()
    {
        var ci = await CreateLaptopAsync();

        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ci.Id}/assignment", "EndUser");
        request.Content = JsonContent.Create(new { ownerUserId = _owner.Id });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListDirectoryUsers_AsEndUser_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/directory/users", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<CiDto> CreateLaptopAsync(string lifecycleState = "InStock")
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "Hardware",
            name = $"Lifecycle laptop {Guid.NewGuid():N}",
            lifecycleState,
            attributes = new Dictionary<string, string> { ["manufacturer"] = "Dell", ["model"] = "5550" },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private async Task<HttpResponseMessage> TransitionAsync(Guid ciId, string targetState, string? note = null)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/cis/{ciId}/lifecycle-transitions");
        request.Content = JsonContent.Create(new { targetState, note });
        return await _client!.SendAsync(request);
    }

    private async Task<CiDto> AssignAsync(
        Guid ciId,
        Guid? ownerUserId,
        Guid? departmentId,
        Guid? siteId,
        string? note = null)
    {
        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ciId}/assignment");
        request.Content = JsonContent.Create(new { ownerUserId, departmentId, siteId, note });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
        request.Headers.Add(LifecycleAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(
        Guid Id,
        string Type,
        string Name,
        bool IsActive,
        string LifecycleState,
        OwnershipDto Ownership);

    private sealed record OwnershipDto(
        Guid? OwnerUserId,
        string? OwnerName,
        Guid? DepartmentId,
        string? DepartmentName,
        Guid? SiteId,
        string? SiteName,
        DateTimeOffset? AssignedAt);

    private sealed record CiPageDto(List<CiDto> Items, int Total, int Page, int PageSize);

    private sealed record LifecycleHistoryDto(
        Guid Id,
        Guid CiId,
        string FromState,
        string ToState,
        string? Note,
        string ActorId,
        DateTimeOffset OccurredAt);

    private sealed record AssignmentDto(
        Guid Id,
        Guid CiId,
        string Action,
        Guid? FromOwnerUserId,
        string? FromOwnerName,
        Guid? ToOwnerUserId,
        string? ToOwnerName,
        Guid? DepartmentId,
        Guid? SiteId,
        string? Note,
        string ActorId,
        DateTimeOffset OccurredAt);

    private sealed record LifecycleStateDto(string State, List<string> AllowedTargets);

    private sealed record DirectoryUserDto(
        Guid Id,
        string Username,
        string DisplayName,
        string Email,
        string Role,
        Guid SiteId,
        string SiteName,
        Guid DepartmentId,
        string DepartmentName);

    private sealed class LifecycleApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public LifecycleApplication(
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
                        options.DefaultAuthenticateScheme = LifecycleAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = LifecycleAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = LifecycleAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, LifecycleAuthenticationHandler>(
                        LifecycleAuthenticationHandler.TestScheme,
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

    private sealed class LifecycleAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "CiLifecycleTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "ci-lifecycle-test-user-id"),
                    new Claim(ClaimTypes.Name, "ci-lifecycle-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
