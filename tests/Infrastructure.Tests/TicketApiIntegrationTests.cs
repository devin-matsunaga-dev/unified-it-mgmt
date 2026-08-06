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
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;
using Platform.Data;

namespace Infrastructure.Tests;

[Collection(InfrastructureCollection.Name)]
public sealed class TicketApiIntegrationTests : IAsyncLifetime
{
    private readonly TicketApplication _application;
    private HttpClient? _client;

    public TicketApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new TicketApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
    }

    [Fact]
    public async Task Tickets_CreateReadUpdate_PersistsAuditAndOutboxEvents()
    {
        var created = await CreateTicketAsync("Requester A");
        Assert.Equal("Critical", created.Priority);
        Assert.StartsWith("INC-", created.Number, StringComparison.Ordinal);

        using var getRequest = Authenticated(HttpMethod.Get, $"/api/tickets/{created.Id}");
        using var getResponse = await _client!.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        using var updateRequest = Authenticated(HttpMethod.Put, $"/api/tickets/{created.Id}");
        updateRequest.Content = JsonContent.Create(new
        {
            title = "Updated printer outage",
            description = "The entire floor is affected.",
            type = "Incident",
            urgency = "Medium",
            impact = "High",
        });
        using var updateResponse = await _client.SendAsync(updateRequest);
        var updated = await updateResponse.Content.ReadFromJsonAsync<TicketDto>();

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.NotNull(updated);
        Assert.Equal("High", updated.Priority);

        await using var scope = _application.Services.CreateAsyncScope();
        var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audits = await platformContext.AuditEntries
            .Where(entry => entry.EntityType == "Ticket" && entry.EntityId == created.Id.ToString())
            .OrderBy(entry => entry.OccurredAt)
            .ToListAsync();
        Assert.Equal(["Created", "Updated"], audits.Select(entry => entry.Action));

        var messages = await platformContext.Set<OutboxMessage>().ToListAsync();
        Assert.Contains(messages, message => message.MessageType.Contains(nameof(Contracts.Events.TicketCreated), StringComparison.Ordinal));
        Assert.Contains(messages, message => message.MessageType.Contains(nameof(Contracts.Events.TicketUpdated), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateTicket_InvalidUrgency_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/tickets");
        request.Content = JsonContent.Create(new
        {
            title = "Invalid priority inputs",
            description = "Urgency is outside the priority matrix.",
            type = "Incident",
            urgency = 99,
            impact = "High",
            requesterId = "requester-a",
        });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetTicket_EndUserCannotReadAnotherRequestersTicket()
    {
        var created = await CreateTicketAsync("other-requester");
        using var request = Authenticated(HttpMethod.Get, $"/api/tickets/{created.Id}", "EndUser");

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HelpdeskMigrations_CurrentModel_HasNoPendingChanges()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();

        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    private async Task<TicketDto> CreateTicketAsync(string requesterId)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/tickets");
        request.Content = JsonContent.Create(new
        {
            title = "Printer outage",
            description = "The printer cannot be reached.",
            type = "Incident",
            urgency = "High",
            impact = "High",
            requesterId,
        });
        using var response = await _client!.SendAsync(request);
        var ticket = await response.Content.ReadFromJsonAsync<TicketDto>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TicketDto>(ticket);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(TicketAuthenticationHandler.RoleHeader, role);
        return request;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _application.DisposeAsync();
    }

    private sealed record TicketDto(Guid Id, string Number, string Priority);

    private sealed class TicketApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;

        public TicketApplication(string connectionString, string rabbitMqConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
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
                    ["Platform:ApplyMigrations"] = "false",
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TicketAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = TicketAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = TicketAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TicketAuthenticationHandler>(
                        TicketAuthenticationHandler.TestScheme,
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

    private sealed class TicketAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "TicketTest";
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
                new Claim(ClaimTypes.Role, role.ToString()),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
