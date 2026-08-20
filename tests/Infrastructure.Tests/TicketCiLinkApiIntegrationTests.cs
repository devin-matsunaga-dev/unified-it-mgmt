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
using Modules.Helpdesk.Data;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The WP's verification, end to end: a CI linked to a ticket shows on both pages, the unlink is
/// audited, and a CI that a ticket still names cannot be deleted.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class TicketCiLinkApiIntegrationTests : IAsyncLifetime
{
    private readonly TicketCiLinkApplication _application;
    private HttpClient? _client;

    public TicketCiLinkApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new TicketCiLinkApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The WP's first verification step: link a CI, then read it back from both directions.</summary>
    [Fact]
    public async Task LinkCi_ShowsOnTheTicketAndOnTheAssetsTicketHistory()
    {
        var ticket = await CreateTicketAsync("Laptop will not charge");
        var ci = await CreateCiAsync("Hardware", "Finance laptop");

        using var linked = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        // The ticket page's linked-CI cards: the CI's live name and state, not a snapshot.
        var links = await GetAsync<List<LinkDto>>($"/api/tickets/{ticket.Id}/cis");
        var link = Assert.Single(links);
        Assert.Equal(ci.Id, link.CiId);
        Assert.Equal(ci.Name, link.CiName);
        Assert.Equal("Hardware", link.CiType);
        Assert.Equal("InStock", link.LifecycleState);
        Assert.Equal("ticket-ci-link-test-user-id", link.LinkedById);

        // The asset page's ticket history: the same fact read from the other side.
        var history = await GetAsync<TicketPageDto>($"/api/tickets?ciId={ci.Id}");
        Assert.Equal(1, history.Total);
        Assert.Equal(ticket.Id, Assert.Single(history.Items).Id);
    }

    /// <summary>A rename on the CMDB side has to reach every ticket that names it — hence no snapshot.</summary>
    [Fact]
    public async Task LinkedCiCard_AfterTheCiIsRenamed_ShowsTheNewName()
    {
        var ticket = await CreateTicketAsync("Host is noisy");
        var ci = await CreateCiAsync("Server", "Old host name");
        using var linked = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        using var rename = Authenticated(HttpMethod.Put, $"/api/cis/{ci.Id}");
        rename.Content = JsonContent.Create(new
        {
            name = "New host name",
            isActive = true,
            attributes = AttributesFor("Server"),
        });
        using var renamed = await _client!.SendAsync(rename);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var links = await GetAsync<List<LinkDto>>($"/api/tickets/{ticket.Id}/cis");
        Assert.Equal("New host name", Assert.Single(links).CiName);
    }

    [Fact]
    public async Task Unlink_RemovesTheLinkFromBothSidesAndIsAudited()
    {
        var ticket = await CreateTicketAsync("Switch port flapping");
        var ci = await CreateCiAsync("NetworkDevice", "Access switch");
        using var linked = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        var created = Assert.IsType<LinkDto>(await linked.Content.ReadFromJsonAsync<LinkDto>());

        using var request = Authenticated(HttpMethod.Delete, $"/api/tickets/{ticket.Id}/cis/{ci.Id}");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Empty(await GetAsync<List<LinkDto>>($"/api/tickets/{ticket.Id}/cis"));
        Assert.Equal(0, (await GetAsync<TicketPageDto>($"/api/tickets?ciId={ci.Id}")).Total);

        await using var scope = _application.Services.CreateAsyncScope();
        var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entityId = created.Id.ToString();
        var entries = await platformContext.AuditEntries
            .Where(entry => entry.EntityType == "TicketCiLink" && entry.EntityId == entityId)
            .ToListAsync();
        Assert.Contains(entries, entry => entry.Action == "Created");
        var deleted = Assert.Single(entries, entry => entry.Action == "Deleted");
        Assert.Equal("ticket-ci-link-test-user-id", deleted.ActorId);

        Assert.Contains(
            await platformContext.Set<OutboxMessage>().ToListAsync(),
            message => message.MessageType.Contains(
                nameof(Contracts.Events.TicketCiUnlinked), StringComparison.Ordinal));
    }

    /// <summary>Failure path: linking the same CI twice is the same fact, not a second one.</summary>
    [Fact]
    public async Task LinkCi_Twice_ReturnsConflict()
    {
        var ticket = await CreateTicketAsync("Printer jam");
        var ci = await CreateCiAsync("Hardware", "Floor printer");
        using var first = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await LinkAsync(ticket.Id, ci.Id);
        var problem = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        Assert.Contains("already linked", problem, StringComparison.Ordinal);
        Assert.Single(await GetAsync<List<LinkDto>>($"/api/tickets/{ticket.Id}/cis"));
    }

    [Fact]
    public async Task LinkCi_ThatDoesNotExist_ReturnsValidationProblem()
    {
        var ticket = await CreateTicketAsync("Ticket about nothing");

        using var response = await LinkAsync(ticket.Id, Guid.CreateVersion7());
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkCi_OnAnUnknownTicket_ReturnsNotFoundProblem()
    {
        var ci = await CreateCiAsync("Hardware", "Unlinked laptop");

        using var response = await LinkAsync(Guid.CreateVersion7(), ci.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unlink_ACiThatIsNotLinked_ReturnsNotFoundProblem()
    {
        var ticket = await CreateTicketAsync("Ticket with no assets");
        var ci = await CreateCiAsync("Hardware", "Laptop nobody linked");

        using var request = Authenticated(HttpMethod.Delete, $"/api/tickets/{ticket.Id}/cis/{ci.Id}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The guard the WP-2.3 notes asked for: without it, deleting a CI would leave every linked ticket
    /// pointing at a row that is gone, and no foreign key can catch it across schemas.
    /// </summary>
    [Fact]
    public async Task DeleteCi_WhileATicketStillLinksIt_ReturnsConflict()
    {
        var ticket = await CreateTicketAsync("Mail relay down");
        var ci = await CreateCiAsync("Server", "Mail relay");
        using var linked = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        using var blocked = Authenticated(HttpMethod.Delete, $"/api/cis/{ci.Id}");
        using var blockedResponse = await _client!.SendAsync(blocked);
        var problem = await blockedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, blockedResponse.StatusCode);
        Assert.Contains("unlink it from its tickets", problem, StringComparison.Ordinal);

        using var unlink = Authenticated(HttpMethod.Delete, $"/api/tickets/{ticket.Id}/cis/{ci.Id}");
        using var unlinked = await _client!.SendAsync(unlink);
        Assert.Equal(HttpStatusCode.NoContent, unlinked.StatusCode);

        using var allowed = Authenticated(HttpMethod.Delete, $"/api/cis/{ci.Id}");
        using var allowedResponse = await _client!.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
    }

    /// <summary>Deleting the ticket takes its links with it — the CI outlives them.</summary>
    [Fact]
    public async Task DeletingATicketsLinks_LeavesTheCiDeletable()
    {
        var ticket = await CreateTicketAsync("Ticket that goes away");
        var ci = await CreateCiAsync("Hardware", "Laptop that stays");
        using var linked = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var helpdesk = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            await helpdesk.Tickets.Where(item => item.Id == ticket.Id).ExecuteDeleteAsync();
        }

        using var request = Authenticated(HttpMethod.Delete, $"/api/cis/{ci.Id}");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>The CMDB is agent-only, so a requester may not see or change a ticket's assets.</summary>
    [Fact]
    public async Task TicketCis_AsEndUser_AreForbidden()
    {
        var ticket = await CreateTicketAsync("Requester's own ticket");
        var ci = await CreateCiAsync("Hardware", "Requester's laptop");
        using var linked = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        using var read = Authenticated(HttpMethod.Get, $"/api/tickets/{ticket.Id}/cis", "EndUser");
        using var readResponse = await _client!.SendAsync(read);
        Assert.Equal(HttpStatusCode.Forbidden, readResponse.StatusCode);
        Assert.Equal("application/problem+json", readResponse.Content.Headers.ContentType?.MediaType);

        using var write = Authenticated(HttpMethod.Post, $"/api/tickets/{ticket.Id}/cis", "EndUser");
        write.Content = JsonContent.Create(new { ciId = ci.Id });
        using var writeResponse = await _client!.SendAsync(write);
        Assert.Equal(HttpStatusCode.Forbidden, writeResponse.StatusCode);
    }

    private Task<HttpResponseMessage> LinkAsync(Guid ticketId, Guid ciId)
    {
        var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticketId}/cis");
        request.Content = JsonContent.Create(new { ciId });
        return _client!.SendAsync(request);
    }

    // ---- WP-3.7: the CMDB context the card carries ----

    /// <summary>
    /// The enrichment as it reaches the browser: warranty status computed by the Assets module, and
    /// the other open tickets about the same CI — never this ticket itself, which would read as the
    /// ticket citing itself as prior art.
    /// </summary>
    [Fact]
    public async Task GetCis_ForALinkedCiWithAWarrantyAndAnotherOpenTicket_CarriesBothOnTheCard()
    {
        var ci = await CreateCiAsync("NetworkDevice", "Branch switch");
        await SetWarrantyAsync(ci.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14));
        var earlier = await CreateTicketAsync("Uplink drops every few minutes");
        using var _ = await LinkAsync(earlier.Id, ci.Id);
        var ticket = await CreateTicketAsync("Switch is unreachable");
        using var linked = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        var card = Assert.Single(await GetAsync<List<LinkDto>>($"/api/tickets/{ticket.Id}/cis"));

        Assert.Equal("ExpiringSoon", card.WarrantyStatus);
        Assert.Equal(14, card.WarrantyDaysRemaining);
        var related = Assert.Single(card.OpenRelatedTickets);
        Assert.Equal(earlier.Id, related.TicketId);
        Assert.Equal(earlier.Number, related.Number);
        Assert.DoesNotContain(card.OpenRelatedTickets, item => item.TicketId == ticket.Id);
    }

    /// <summary>
    /// A CI nobody has recorded a warranty for says nothing about one, rather than reporting a status
    /// it has no date to compute.
    /// </summary>
    [Fact]
    public async Task GetCis_ForALinkedCiWithNoWarranty_LeavesTheWarrantyFieldsEmpty()
    {
        var ticket = await CreateTicketAsync("Laptop fan is loud");
        var ci = await CreateCiAsync("Hardware", "Support laptop");
        using var linked = await LinkAsync(ticket.Id, ci.Id);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        var card = Assert.Single(await GetAsync<List<LinkDto>>($"/api/tickets/{ticket.Id}/cis"));

        Assert.Null(card.WarrantyStatus);
        Assert.Null(card.WarrantyExpiresAt);
        Assert.Null(card.WarrantyDaysRemaining);
        Assert.Empty(card.OpenRelatedTickets);
    }

    /// <summary>
    /// The field surface's reason for CiIds: a technician standing at the asset raises the ticket in
    /// one call, because on the connection a plant room has, a follow-up link request is the one that
    /// fails — leaving a ticket that names no asset and a technician who believes it does.
    /// </summary>
    [Fact]
    public async Task CreateTicket_WithCiIds_LinksTheCiInTheSameCall()
    {
        var ci = await CreateCiAsync("Hardware", "Stockroom laptop");

        using var request = Authenticated(HttpMethod.Post, "/api/tickets");
        request.Content = JsonContent.Create(new
        {
            title = "Will not power on",
            description = "No lights, no fan.",
            type = "Incident",
            urgency = "Medium",
            impact = "Medium",
            ciIds = new[] { ci.Id },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = Assert.IsType<TicketDto>(await response.Content.ReadFromJsonAsync<TicketDto>());

        var links = await GetAsync<List<LinkDto>>($"/api/tickets/{ticket.Id}/cis");
        Assert.Equal(ci.Id, Assert.Single(links).CiId);
    }

    /// <summary>The failure path: an unknown CI id is the caller's mistake and no ticket is written.</summary>
    [Fact]
    public async Task CreateTicket_WithAnUnknownCiId_IsRefusedAndWritesNoTicket()
    {
        var title = $"Ticket that should not exist {Guid.CreateVersion7()}";
        using var request = Authenticated(HttpMethod.Post, "/api/tickets");
        request.Content = JsonContent.Create(new
        {
            title,
            description = "Raised against a CI that does not exist.",
            type = "Incident",
            urgency = "Medium",
            impact = "Medium",
            ciIds = new[] { Guid.CreateVersion7() },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.False(await dbContext.Tickets.AnyAsync(item => item.Title == title));
    }

    private async Task SetWarrantyAsync(Guid ciId, DateOnly expiresAt)
    {
        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ciId}/coverage");
        request.Content = JsonContent.Create(new { warrantyExpiresAt = expiresAt });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<TicketDto> CreateTicketAsync(string title)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/tickets");
        request.Content = JsonContent.Create(new
        {
            title,
            description = "Raised by the ticket↔asset integration test.",
            type = "Incident",
            urgency = "Medium",
            impact = "Medium",
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TicketDto>(await response.Content.ReadFromJsonAsync<TicketDto>());
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
        "Hardware" => new() { ["manufacturer"] = "Dell", ["model"] = "Latitude 5450" },
        "NetworkDevice" => new() { ["managementIp"] = "10.0.0.2", ["vendor"] = "Cisco", ["portCount"] = "24" },
        "Server" => new()
        {
            ["hostname"] = $"host-{Guid.NewGuid():N}"[..20],
            ["operatingSystem"] = "Ubuntu 24.04",
            ["cpuCores"] = "16",
            ["ramGb"] = "128",
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
        request.Headers.Add(TicketCiLinkAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record TicketDto(Guid Id, string Number, string Title, string Status);

    private sealed record TicketPageDto(List<TicketDto> Items, int Total, int Page, int PageSize);

    private sealed record CiDto(Guid Id, string Type, string Name, string LifecycleState);

    private sealed record LinkDto(
        Guid Id,
        Guid TicketId,
        Guid CiId,
        string CiName,
        string CiType,
        string? AssetTag,
        string? SerialNumber,
        string LifecycleState,
        bool IsActive,
        string? OwnerName,
        string? SiteName,
        string? DepartmentName,
        DateOnly? WarrantyExpiresAt,
        string? WarrantyStatus,
        int? WarrantyDaysRemaining,
        string? ContractName,
        List<RelatedTicketDto> OpenRelatedTickets,
        string LinkedById,
        string LinkedByName,
        DateTimeOffset LinkedAt);

    private sealed record RelatedTicketDto(
        Guid TicketId,
        string Number,
        string Title,
        string Status,
        string Priority,
        DateTimeOffset CreatedAt);

    private sealed class TicketCiLinkApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public TicketCiLinkApplication(
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
                        options.DefaultAuthenticateScheme = TicketCiLinkAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = TicketCiLinkAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = TicketCiLinkAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TicketCiLinkAuthenticationHandler>(
                        TicketCiLinkAuthenticationHandler.TestScheme,
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

    private sealed class TicketCiLinkAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "TicketCiLinkTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "ticket-ci-link-test-user-id"),
                    new Claim(ClaimTypes.Name, "ticket-ci-link-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
