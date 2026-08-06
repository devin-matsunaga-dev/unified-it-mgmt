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
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
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
    public async Task StatusWorkflow_LegalTransitions_PersistHistoryAuditAndOutboxEvent()
    {
        var created = await CreateTicketAsync("workflow-requester");
        Assert.Equal("New", created.Status);

        var triaged = await TransitionAsync(created.Id, "Triage");
        var inProgress = await TransitionAsync(created.Id, "InProgress");

        Assert.Equal("Triage", triaged.Status);
        Assert.Equal("InProgress", inProgress.Status);

        using var historyRequest = Authenticated(HttpMethod.Get, $"/api/tickets/{created.Id}/transitions");
        using var historyResponse = await _client!.SendAsync(historyRequest);
        var history = await historyResponse.Content.ReadFromJsonAsync<List<TransitionDto>>();

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.NotNull(history);
        Assert.Collection(
            history,
            transition =>
            {
                Assert.Equal("New", transition.FromStatus);
                Assert.Equal("Triage", transition.ToStatus);
            },
            transition =>
            {
                Assert.Equal("Triage", transition.FromStatus);
                Assert.Equal("InProgress", transition.ToStatus);
            });

        await using var scope = _application.Services.CreateAsyncScope();
        var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Contains(
            await platformContext.AuditEntries.Where(entry => entry.EntityId == created.Id.ToString()).ToListAsync(),
            entry => entry.Action == "StatusChanged");
        Assert.Contains(
            await platformContext.Set<OutboxMessage>().ToListAsync(),
            message => message.MessageType.Contains(nameof(Contracts.Events.TicketStatusChanged), StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusWorkflow_IllegalTransition_ReturnsConflictWithoutHistory()
    {
        var created = await CreateTicketAsync("illegal-transition-requester");
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{created.Id}/transitions");
        request.Content = JsonContent.Create(new { targetStatus = "Pending", resolutionNote = (string?)null });

        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Transition from 'New' to 'Pending' is not allowed.", problem, StringComparison.Ordinal);

        await using var scope = _application.Services.CreateAsyncScope();
        var historyCount = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .TicketTransitionHistory.CountAsync(history => history.TicketId == created.Id);
        Assert.Equal(0, historyCount);
    }

    [Fact]
    public async Task StatusWorkflow_ResolveWithoutNote_ReturnsValidationProblem()
    {
        var created = await CreateTicketAsync("resolution-requester");
        await TransitionAsync(created.Id, "Triage");
        await TransitionAsync(created.Id, "InProgress");
        await TransitionAsync(created.Id, "Pending");
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{created.Id}/transitions");
        request.Content = JsonContent.Create(new { targetStatus = "Resolved", resolutionNote = "  " });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var resolved = await TransitionAsync(created.Id, "Resolved", "Replaced the failed print server.");
        Assert.Equal("Resolved", resolved.Status);

        using var historyRequest = Authenticated(HttpMethod.Get, $"/api/tickets/{created.Id}/transitions");
        using var historyResponse = await _client.SendAsync(historyRequest);
        var history = await historyResponse.Content.ReadFromJsonAsync<List<TransitionDto>>();
        Assert.Equal(4, history?.Count);
        Assert.Equal("Replaced the failed print server.", history?[^1].ResolutionNote);
    }

    [Fact]
    public async Task QueueAssignment_ThreeTicketsAcrossTwoTechnicians_AlternatesAndSupportsReassignmentAndMine()
    {
        var queueId = await CreateQueueWithMembersAsync("tech-a", "tech-b");

        var first = await CreateTicketAsync("queue-requester-1", queueId);
        var second = await CreateTicketAsync("queue-requester-2", queueId);
        var third = await CreateTicketAsync("queue-requester-3", queueId);

        Assert.Equal("tech-a", first.AssignedTechnicianId);
        Assert.Equal("tech-b", second.AssignedTechnicianId);
        Assert.Equal("tech-a", third.AssignedTechnicianId);

        using var reassignRequest = Authenticated(
            HttpMethod.Post, $"/api/tickets/{first.Id}/assignments", userId: "lead-tech");
        reassignRequest.Content = JsonContent.Create(new { technicianId = "tech-b" });
        using var reassignResponse = await _client!.SendAsync(reassignRequest);
        Assert.Equal(HttpStatusCode.OK, reassignResponse.StatusCode);

        using var historyRequest = Authenticated(HttpMethod.Get, $"/api/tickets/{first.Id}/assignments");
        using var historyResponse = await _client.SendAsync(historyRequest);
        var history = await historyResponse.Content.ReadFromJsonAsync<List<AssignmentDto>>();
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.Collection(
            Assert.IsType<List<AssignmentDto>>(history),
            assignment =>
            {
                Assert.Null(assignment.FromTechnicianId);
                Assert.Equal("tech-a", assignment.ToTechnicianId);
                Assert.Equal("Automatic", assignment.Kind);
            },
            assignment =>
            {
                Assert.Equal("tech-a", assignment.FromTechnicianId);
                Assert.Equal("tech-b", assignment.ToTechnicianId);
                Assert.Equal("Manual", assignment.Kind);
            });

        var techATickets = await GetMineAsync("tech-a");
        var techBTickets = await GetMineAsync("tech-b");
        Assert.Equal([third.Id], techATickets.Items.Select(ticket => ticket.Id));
        Assert.Equal(2, techBTickets.Total);
        Assert.Contains(techBTickets.Items, ticket => ticket.Id == first.Id);
        Assert.Contains(techBTickets.Items, ticket => ticket.Id == second.Id);

        await using var scope = _application.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().AuditEntries
            .Where(entry => entry.EntityType == "Ticket" && entry.EntityId == first.Id.ToString()).ToListAsync();
        Assert.Contains(audits, entry => entry.Action == "Assigned");
    }

    [Fact]
    public async Task QueueAssignment_TechnicianOutsideQueueTeam_ReturnsValidationProblemWithoutHistoryEntry()
    {
        var queueId = await CreateQueueWithMembersAsync("member-tech");
        var ticket = await CreateTicketAsync("invalid-assignment-requester", queueId);
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticket.Id}/assignments");
        request.Content = JsonContent.Create(new { technicianId = "outside-tech" });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await using var scope = _application.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .TicketAssignmentHistory.CountAsync(history => history.TicketId == ticket.Id));
    }

    [Fact]
    public async Task Comments_InternalAndPublic_EndUserReadsOnlyPublicComment()
    {
        var ticket = await CreateTicketAsync("comment-requester");
        await AddCommentAsync(ticket.Id, "Visible reply", false);
        await AddCommentAsync(ticket.Id, "Technician-only note", true);

        using var technicianRequest = Authenticated(HttpMethod.Get, $"/api/tickets/{ticket.Id}/comments");
        using var technicianResponse = await _client!.SendAsync(technicianRequest);
        var technicianComments = await technicianResponse.Content.ReadFromJsonAsync<List<CommentDto>>();
        Assert.Equal(2, technicianComments?.Count);

        using var endUserRequest = Authenticated(
            HttpMethod.Get, $"/api/tickets/{ticket.Id}/comments", "EndUser", "comment-requester");
        using var endUserResponse = await _client.SendAsync(endUserRequest);
        var endUserComments = await endUserResponse.Content.ReadFromJsonAsync<List<CommentDto>>();

        Assert.Equal(HttpStatusCode.OK, endUserResponse.StatusCode);
        var comment = Assert.Single(Assert.IsType<List<CommentDto>>(endUserComments));
        Assert.Equal("Visible reply", comment.Body);
        Assert.False(comment.IsInternal);
    }

    [Fact]
    public async Task Comments_EndUserCreatesInternalComment_ReturnsForbidden()
    {
        var ticket = await CreateTicketAsync("internal-comment-requester");
        using var request = Authenticated(
            HttpMethod.Post, $"/api/tickets/{ticket.Id}/comments", "EndUser", "internal-comment-requester");
        request.Content = JsonContent.Create(new { body = "Should be rejected", isInternal = true });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Worklogs_ValidAndInvalidMinutes_PersistsValidEntryAndRejectsInvalidEntry()
    {
        var ticket = await CreateTicketAsync("worklog-requester");
        using var validRequest = Authenticated(HttpMethod.Post, $"/api/tickets/{ticket.Id}/worklogs");
        validRequest.Content = JsonContent.Create(new { minutes = 45, note = "Diagnosed printer queue." });
        using var validResponse = await _client!.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.Created, validResponse.StatusCode);

        using var invalidRequest = Authenticated(HttpMethod.Post, $"/api/tickets/{ticket.Id}/worklogs");
        invalidRequest.Content = JsonContent.Create(new { minutes = 0, note = "Invalid" });
        using var invalidResponse = await _client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        using var listRequest = Authenticated(HttpMethod.Get, $"/api/tickets/{ticket.Id}/worklogs");
        using var listResponse = await _client.SendAsync(listRequest);
        var worklogs = await listResponse.Content.ReadFromJsonAsync<List<WorklogDto>>();
        Assert.Equal(45, Assert.Single(Assert.IsType<List<WorklogDto>>(worklogs)).Minutes);
    }

    [Fact]
    public async Task Attachments_TenMegabyteAllowedFile_UploadsAndDownloadsFromMinio()
    {
        var ticket = await CreateTicketAsync("attachment-requester");
        var expected = new byte[10 * 1024 * 1024];
        Random.Shared.NextBytes(expected);
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticket.Id}/attachments");
        request.Content = Multipart(expected, "evidence.pdf", "application/pdf");

        using var response = await _client!.SendAsync(request);
        var attachment = await response.Content.ReadFromJsonAsync<AttachmentDto>();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(expected.Length, attachment?.Size);

        using var downloadRequest = Authenticated(HttpMethod.Get, Assert.IsType<AttachmentDto>(attachment).DownloadUrl);
        using var downloadResponse = await _client.SendAsync(downloadRequest);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal(expected, await downloadResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("evidence.pdf", downloadResponse.Content.Headers.ContentDisposition?.FileNameStar, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attachments_BlockedExtension_ReturnsValidationProblemWithoutMetadata()
    {
        var ticket = await CreateTicketAsync("blocked-attachment-requester");
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticket.Id}/attachments");
        request.Content = Multipart([1, 2, 3], "malware.exe", "application/octet-stream");

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await using var scope = _application.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .TicketAttachments.AnyAsync(attachment => attachment.TicketId == ticket.Id));
    }

    [Fact]
    public async Task HelpdeskMigrations_CurrentModel_HasNoPendingChanges()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();

        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    private async Task<TicketDto> CreateTicketAsync(string requesterId, Guid? queueId = null)
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
            queueId,
        });
        using var response = await _client!.SendAsync(request);
        var ticket = await response.Content.ReadFromJsonAsync<TicketDto>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TicketDto>(ticket);
    }

    private async Task AddCommentAsync(Guid ticketId, string body, bool isInternal)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticketId}/comments");
        request.Content = JsonContent.Create(new { body, isInternal });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static MultipartFormDataContent Multipart(byte[] content, string fileName, string contentType)
    {
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new(contentType);
        multipart.Add(file, "file", fileName);
        return multipart;
    }

    private async Task<Guid> CreateQueueWithMembersAsync(params string[] technicianIds)
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var teamRequest = Authenticated(HttpMethod.Post, "/api/teams");
        teamRequest.Content = JsonContent.Create(new { name = $"Team {suffix}" });
        using var teamResponse = await _client!.SendAsync(teamRequest);
        var team = await teamResponse.Content.ReadFromJsonAsync<TeamDto>();
        Assert.Equal(HttpStatusCode.Created, teamResponse.StatusCode);

        foreach (var technicianId in technicianIds)
        {
            using var memberRequest = Authenticated(HttpMethod.Post, $"/api/teams/{team!.Id}/members");
            memberRequest.Content = JsonContent.Create(new { technicianId });
            using var memberResponse = await _client.SendAsync(memberRequest);
            Assert.Equal(HttpStatusCode.NoContent, memberResponse.StatusCode);
        }

        using var queueRequest = Authenticated(HttpMethod.Post, "/api/queues");
        queueRequest.Content = JsonContent.Create(new { name = $"Queue {suffix}", teamId = team!.Id });
        using var queueResponse = await _client.SendAsync(queueRequest);
        var queue = await queueResponse.Content.ReadFromJsonAsync<QueueDto>();
        Assert.Equal(HttpStatusCode.Created, queueResponse.StatusCode);
        return Assert.IsType<QueueDto>(queue).Id;
    }

    private async Task<TicketPageDto> GetMineAsync(string userId)
    {
        using var request = Authenticated(HttpMethod.Get, "/api/tickets/mine", userId: userId);
        using var response = await _client!.SendAsync(request);
        var page = await response.Content.ReadFromJsonAsync<TicketPageDto>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<TicketPageDto>(page);
    }

    private async Task<TicketDto> TransitionAsync(Guid ticketId, string targetStatus, string? resolutionNote = null)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticketId}/transitions");
        request.Content = JsonContent.Create(new { targetStatus, resolutionNote });
        using var response = await _client!.SendAsync(request);
        var ticket = await response.Content.ReadFromJsonAsync<TicketDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<TicketDto>(ticket);
    }

    private static HttpRequestMessage Authenticated(
        HttpMethod method, string uri, string role = "Technician", string userId = "test-user-id")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(TicketAuthenticationHandler.RoleHeader, role);
        request.Headers.Add(TicketAuthenticationHandler.UserIdHeader, userId);
        return request;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _application.DisposeAsync();
    }

    private sealed record TicketDto(Guid Id, string Number, string Priority, string Status, string? AssignedTechnicianId);

    private sealed record TeamDto(Guid Id);
    private sealed record QueueDto(Guid Id);
    private sealed record TicketPageDto(IReadOnlyList<TicketDto> Items, int Total);
    private sealed record AssignmentDto(string? FromTechnicianId, string ToTechnicianId, string Kind);
    private sealed record CommentDto(string Body, bool IsInternal);
    private sealed record WorklogDto(int Minutes);
    private sealed record AttachmentDto(Guid Id, long Size, string DownloadUrl);

    private sealed record TransitionDto(string FromStatus, string ToStatus, string? ResolutionNote);

    private sealed class TicketApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public TicketApplication(string connectionString, string rabbitMqConnectionString, string minioConnectionString)
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
        public const string UserIdHeader = "X-Test-User-Id";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userId = Request.Headers.TryGetValue(UserIdHeader, out var requestedUserId)
                ? requestedUserId.ToString()
                : "test-user-id";
            var claims = new[]
            {
                new Claim("sub", userId),
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Role, role.ToString()),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
