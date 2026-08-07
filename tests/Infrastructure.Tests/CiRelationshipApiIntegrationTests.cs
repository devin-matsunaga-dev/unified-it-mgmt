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

namespace Infrastructure.Tests;

/// <summary>
/// The WP's verification chain, end to end: VM→Host→Switch→Router built through the API, walked in
/// both directions, and then deliberately closed into a cycle.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CiRelationshipApiIntegrationTests : IAsyncLifetime
{
    private readonly RelationshipApplication _application;
    private HttpClient? _client;

    public CiRelationshipApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new RelationshipApplication(
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The WP's first verification step: build VM→Host→Switch→Router, then ask what a router outage
    /// takes with it.
    /// </summary>
    [Fact]
    public async Task ImpactedBy_RouterAtTheTopOfTheChain_ReturnsEveryCiBeneathIt()
    {
        var chain = await BuildChainAsync();

        var impact = await GetAsync<GraphDto>($"/api/cis/{chain.Router.Id}/impacted-by");

        Assert.Equal(chain.Router.Id, impact.RootCiId);
        Assert.False(impact.ContainsCycle);
        Assert.Equal(
            [chain.Router.Id, chain.Switch.Id, chain.Host.Id, chain.Vm.Id],
            impact.Nodes.OrderBy(node => node.Depth).Select(node => node.Id));
        Assert.Equal([0, 1, 2, 3], impact.Nodes.Select(node => node.Depth).Order());
        Assert.Equal(3, impact.Edges.Count);
    }

    [Fact]
    public async Task Ancestors_FromTheVm_WalksUpToTheRouter()
    {
        var chain = await BuildChainAsync();

        var ancestors = await GetAsync<GraphDto>($"/api/cis/{chain.Vm.Id}/ancestors");

        // Ancestors are what the VM depends on, so the root itself is not part of the answer.
        Assert.Equal("Ancestors", ancestors.Direction);
        Assert.DoesNotContain(ancestors.Nodes, node => node.Id == chain.Vm.Id);
        Assert.Equal(
            [(chain.Host.Id, 1), (chain.Switch.Id, 2), (chain.Router.Id, 3)],
            ancestors.Nodes.OrderBy(node => node.Depth).Select(node => (node.Id, node.Depth)));
    }

    [Fact]
    public async Task Descendants_FromTheSwitch_ReturnsOnlyWhatSitsBeneathIt()
    {
        var chain = await BuildChainAsync();

        var descendants = await GetAsync<GraphDto>($"/api/cis/{chain.Switch.Id}/descendants");

        Assert.Equal(
            [(chain.Host.Id, 1), (chain.Vm.Id, 2)],
            descendants.Nodes.OrderBy(node => node.Depth).Select(node => (node.Id, node.Depth)));
        Assert.DoesNotContain(descendants.Nodes, node => node.Id == chain.Router.Id);
    }

    [Fact]
    public async Task Ancestors_WithMaxDepth_StopsAndSaysSo()
    {
        var chain = await BuildChainAsync();

        var shallow = await GetAsync<GraphDto>($"/api/cis/{chain.Vm.Id}/ancestors?maxDepth=2");

        Assert.True(shallow.MaxDepthReached);
        Assert.Equal(2, shallow.MaxDepth);
        Assert.Equal([chain.Host.Id, chain.Switch.Id], shallow.Nodes.OrderBy(node => node.Depth).Select(node => node.Id));
        Assert.DoesNotContain(shallow.Nodes, node => node.Id == chain.Router.Id);
    }

    /// <summary>
    /// The WP's second verification step. The documented choice is to accept the cycle and traverse
    /// it safely: the walk visits each CI once, terminates, and reports that a cycle is there.
    /// </summary>
    [Fact]
    public async Task ImpactedBy_WhenTheChainIsClosedIntoACycle_TerminatesAndReportsIt()
    {
        var chain = await BuildChainAsync();

        // Close the loop: the router now depends on the VM it serves.
        using var closing = await RelateAsync(chain.Router.Id, chain.Vm.Id, "DependsOn");
        Assert.Equal(HttpStatusCode.Created, closing.StatusCode);

        var impact = await GetAsync<GraphDto>($"/api/cis/{chain.Router.Id}/impacted-by");

        Assert.True(impact.ContainsCycle);
        // Every CI appears exactly once despite the loop, and the closing edge is part of the picture.
        Assert.Equal(4, impact.Nodes.Count);
        Assert.Equal(4, impact.Nodes.Select(node => node.Id).Distinct().Count());
        Assert.Equal(4, impact.Edges.Count);

        var ancestors = await GetAsync<GraphDto>($"/api/cis/{chain.Vm.Id}/ancestors");
        Assert.True(ancestors.ContainsCycle);
        Assert.Equal(3, ancestors.Nodes.Count);
    }

    [Fact]
    public async Task GetRelationships_SplitsTheDirectEdgesByEnd()
    {
        var chain = await BuildChainAsync();

        var relationships = await GetAsync<RelationshipsDto>($"/api/cis/{chain.Host.Id}/relationships");

        var upstream = Assert.Single(relationships.Upstream);
        Assert.Equal(chain.Switch.Id, upstream.TargetCiId);
        Assert.Equal("ConnectsTo", upstream.Type);
        Assert.Equal(chain.Switch.Name, upstream.TargetCiName);

        var downstream = Assert.Single(relationships.Downstream);
        Assert.Equal(chain.Vm.Id, downstream.SourceCiId);
        Assert.Equal("RunsOn", downstream.Type);
    }

    [Fact]
    public async Task CreateRelationship_AuditsAndPublishesThroughTheOutbox()
    {
        var chain = await BuildChainAsync();
        var runsOn = (await GetAsync<RelationshipsDto>($"/api/cis/{chain.Vm.Id}/relationships")).Upstream[0];

        await using var scope = _application.Services.CreateAsyncScope();
        var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entityId = runsOn.Id.ToString();
        var created = await platformContext.AuditEntries
            .SingleAsync(entry => entry.EntityType == "CiRelationship" && entry.EntityId == entityId
                && entry.Action == "Created");

        Assert.Equal("ci-relationship-test-user-id", created.ActorId);
        Assert.Contains(
            await platformContext.Set<OutboxMessage>().ToListAsync(),
            message => message.MessageType.Contains(
                nameof(Contracts.Events.CiRelationshipCreated), StringComparison.Ordinal));

        // Deleting one leaves the chain shorter and records the removal.
        using var request = Authenticated(HttpMethod.Delete, $"/api/ci-relationships/{runsOn.Id}");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await GetAsync<GraphDto>($"/api/cis/{chain.Vm.Id}/ancestors");
        Assert.Empty(after.Nodes);
        Assert.Contains(
            await platformContext.AuditEntries.ToListAsync(),
            entry => entry.EntityType == "CiRelationship" && entry.EntityId == entityId
                && entry.Action == "Deleted");
    }

    /// <summary>Failure path: a CI cannot depend on itself.</summary>
    [Fact]
    public async Task CreateRelationship_PointingAtItself_ReturnsValidationProblem()
    {
        var ci = await CreateCiAsync("Server", "Self-referential server");

        using var response = await RelateAsync(ci.Id, ci.Id, "DependsOn");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("cannot be related to itself", problem, StringComparison.Ordinal);
        Assert.Empty((await GetAsync<RelationshipsDto>($"/api/cis/{ci.Id}/relationships")).Upstream);
    }

    [Fact]
    public async Task CreateRelationship_Twice_ReturnsConflict()
    {
        var chain = await BuildChainAsync();

        using var response = await RelateAsync(chain.Vm.Id, chain.Host.Id, "RunsOn");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already RunsOn", problem, StringComparison.Ordinal);

        // The same pair related a different way is a distinct fact and is accepted.
        using var other = await RelateAsync(chain.Vm.Id, chain.Host.Id, "DependsOn");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task CreateRelationship_UnknownTarget_ReturnsValidationProblem()
    {
        var ci = await CreateCiAsync("Server", "Server with a missing neighbour");

        using var response = await RelateAsync(ci.Id, Guid.CreateVersion7(), "DependsOn");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRelationship_UnknownSource_ReturnsNotFoundProblem()
    {
        var target = await CreateCiAsync("Server", "Target that does exist");

        using var response = await RelateAsync(Guid.CreateVersion7(), target.Id, "DependsOn");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CreateRelationship_OnADisposedCi_ReturnsConflict()
    {
        var disposed = await CreateCiAsync("Server", "Server that left the estate");
        foreach (var state in (string[])["Deployed", "Retired", "Disposed"])
        {
            using var transition = Authenticated(HttpMethod.Post, $"/api/cis/{disposed.Id}/lifecycle-transitions");
            transition.Content = JsonContent.Create(new { targetState = state });
            using var moved = await _client!.SendAsync(transition);
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        var live = await CreateCiAsync("Virtual", "VM that wants the disposed host");

        using var response = await RelateAsync(live.Id, disposed.Id, "RunsOn");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("disposed CI cannot gain new relationships", problem, StringComparison.Ordinal);
    }

    /// <summary>Deleting a related CI would orphan the graph, so it is refused until the edges go.</summary>
    [Fact]
    public async Task DeleteCi_WhileItStillHasRelationships_ReturnsConflict()
    {
        var chain = await BuildChainAsync();

        using var blocked = Authenticated(HttpMethod.Delete, $"/api/cis/{chain.Host.Id}");
        using var blockedResponse = await _client!.SendAsync(blocked);
        var problem = await blockedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, blockedResponse.StatusCode);
        Assert.Contains("Remove the CI's relationships", problem, StringComparison.Ordinal);

        var relationships = await GetAsync<RelationshipsDto>($"/api/cis/{chain.Host.Id}/relationships");
        foreach (var edge in relationships.Upstream.Concat(relationships.Downstream))
        {
            using var request = Authenticated(HttpMethod.Delete, $"/api/ci-relationships/{edge.Id}");
            using var response = await _client!.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using var allowed = Authenticated(HttpMethod.Delete, $"/api/cis/{chain.Host.Id}");
        using var allowedResponse = await _client!.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
    }

    [Fact]
    public async Task Graph_ForAnUnknownCi_ReturnsNotFoundProblem()
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{Guid.CreateVersion7()}/impacted-by");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Relationships_AsEndUser_AreForbidden()
    {
        var chain = await BuildChainAsync();

        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{chain.Router.Id}/impacted-by", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>VM→Host→Switch→Router, exactly the chain the WP asks to be built.</summary>
    private async Task<Chain> BuildChainAsync()
    {
        var router = await CreateCiAsync("NetworkDevice", "Router");
        var switchCi = await CreateCiAsync("NetworkDevice", "Switch");
        var host = await CreateCiAsync("Server", "Host");
        var vm = await CreateCiAsync("Virtual", "VM");

        foreach (var (source, target, type) in ((Guid, Guid, string)[])
                 [
                     (vm.Id, host.Id, "RunsOn"),
                     (host.Id, switchCi.Id, "ConnectsTo"),
                     (switchCi.Id, router.Id, "ConnectsTo"),
                 ])
        {
            using var response = await RelateAsync(source, target, type);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return new(vm, host, switchCi, router);
    }

    private Task<HttpResponseMessage> RelateAsync(Guid sourceCiId, Guid targetCiId, string type)
    {
        var request = Authenticated(HttpMethod.Post, $"/api/cis/{sourceCiId}/relationships");
        request.Content = JsonContent.Create(new { targetCiId, type });
        return _client!.SendAsync(request);
    }

    private async Task<CiDto> CreateCiAsync(string type, string name)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type,
            name = $"{name} {Guid.NewGuid():N}",
            attributes = AttributesFor(type),
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private static Dictionary<string, string> AttributesFor(string type) => type switch
    {
        "NetworkDevice" => new() { ["managementIp"] = "10.0.0.1", ["vendor"] = "Cisco", ["portCount"] = "48" },
        "Server" => new()
        {
            ["hostname"] = $"host-{Guid.NewGuid():N}"[..20],
            ["operatingSystem"] = "Ubuntu 24.04",
            ["cpuCores"] = "16",
            ["ramGb"] = "128",
        },
        "Virtual" => new()
        {
            ["hostname"] = $"vm-{Guid.NewGuid():N}"[..20],
            ["hypervisor"] = "KVM",
            ["vcpuCores"] = "4",
            ["ramGb"] = "16",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No fixture attributes for this CI type."),
    };

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
        request.Headers.Add(RelationshipAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record Chain(CiDto Vm, CiDto Host, CiDto Switch, CiDto Router);

    private sealed record CiDto(Guid Id, string Type, string Name, string LifecycleState);

    private sealed record RelationshipDto(
        Guid Id,
        Guid SourceCiId,
        string SourceCiName,
        Guid TargetCiId,
        string TargetCiName,
        string Type,
        string? Description,
        string CreatedBy,
        DateTimeOffset CreatedAt);

    private sealed record RelationshipsDto(
        Guid CiId,
        List<RelationshipDto> Upstream,
        List<RelationshipDto> Downstream);

    private sealed record GraphNodeDto(Guid Id, string Type, string Name, string LifecycleState, int Depth);

    private sealed record GraphEdgeDto(Guid Id, Guid SourceCiId, Guid TargetCiId, string Type);

    private sealed record GraphDto(
        Guid RootCiId,
        string Direction,
        int MaxDepth,
        bool MaxDepthReached,
        bool ContainsCycle,
        List<GraphNodeDto> Nodes,
        List<GraphEdgeDto> Edges);

    private sealed class RelationshipApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public RelationshipApplication(
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
                        options.DefaultAuthenticateScheme = RelationshipAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = RelationshipAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = RelationshipAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, RelationshipAuthenticationHandler>(
                        RelationshipAuthenticationHandler.TestScheme,
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

    private sealed class RelationshipAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "CiRelationshipTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "ci-relationship-test-user-id"),
                    new Claim(ClaimTypes.Name, "ci-relationship-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
