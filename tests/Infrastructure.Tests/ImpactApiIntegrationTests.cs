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
using Modules.Monitoring.Data;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The blast radius end to end: a hypervisor host, the VMs racked on it, a service behind one of them,
/// the tickets already open on those, and the SLA clocks behind the tickets — all coming back off one
/// call to <c>GET /api/cis/{id}/impact</c>.
/// <para>
/// The arithmetic itself is asserted in <see cref="ImpactAnalyzerTests"/> against a hand-written tree.
/// What this class exists to prove is the plumbing that test cannot see: the recursive walk, the
/// cross-module read of Helpdesk through <c>ITicketImpactDirectory</c>, and the JSON that reaches the
/// browser.
/// </para>
/// <para>
/// Unlike the drift and audit suites this one needs no site of its own. The read is rooted at one CI and
/// reaches only what depends on it, so it cannot compete with what other classes have written to the
/// shared database.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ImpactApiIntegrationTests : IAsyncLifetime
{
    private readonly ImpactApplication _application;
    private HttpClient? _client;

    public ImpactApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        _application = new ImpactApplication(
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
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();

        // The port trap, for the eighth time: creating and deleting CIs reaches Monitoring through
        // IMonitoredAddressDirectory, and an unmigrated schema behind it answers 500 from a query that
        // mentions neither this feature nor that module.
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _application.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The WP's own verification step: the blast radius of a host lists its VMs, the tickets on them and
    /// the departments that own them.
    /// </summary>
    [Fact]
    public async Task Impact_ForAHostCarryingVms_ListsTheVmsTheirTicketsAndTheOwningDepartments()
    {
        var estate = await BuildEstateAsync();

        var impact = await GetAsync<ImpactDto>($"/api/cis/{estate.Host}/impact");

        Assert.Equal(estate.Host, impact.RootCiId);
        Assert.Equal(4, impact.Summary.CiCount);
        Assert.Equal(2, impact.Summary.DirectCiCount);
        Assert.Contains(impact.Cis, ci => ci.CiId == estate.VmApp && ci.Depth == 1);
        Assert.Contains(impact.Cis, ci => ci.CiId == estate.VmWeb && ci.Depth == 1);
        Assert.Contains(impact.Cis, ci => ci.CiId == estate.Service && ci.Depth == 2);

        Assert.Equal(2, impact.Summary.OpenTicketCount);
        Assert.Contains(impact.Tickets, ticket => ticket.CiId == estate.VmApp);
        Assert.Contains(impact.Tickets, ticket => ticket.CiId == estate.Service);

        Assert.Equal(2, impact.Summary.AffectedDepartmentCount);
        Assert.Contains(impact.Departments, department => department.Name == "Finance" && department.CiCount == 2);
        Assert.Contains(impact.Users, user => user.Name == "Alex Doe");
    }

    /// <summary>
    /// The root is part of its own outage. A blast radius that started one hop out would report a dead
    /// host as costing nothing when nothing happens to be racked on it.
    /// </summary>
    [Fact]
    public async Task Impact_IncludesTheCiItselfAtDepthZero()
    {
        var estate = await BuildEstateAsync();

        var impact = await GetAsync<ImpactDto>($"/api/cis/{estate.Host}/impact");

        var root = Assert.Single(impact.Cis, ci => ci.Depth == 0);
        Assert.Equal(estate.Host, root.CiId);
    }

    /// <summary>
    /// The SLA exposure is the number this feature exists to surface: an operator deciding whether to
    /// touch a host at four o'clock wants to know what is already past its deadline behind it.
    /// </summary>
    [Fact]
    public async Task Impact_ReportsTheSlaExposureOfTheTicketsAlreadyOpenOnTheRadius()
    {
        var estate = await BuildEstateAsync();

        var impact = await GetAsync<ImpactDto>($"/api/cis/{estate.Host}/impact");

        Assert.Equal(1, impact.Summary.BreachedSlaCount);
        var breached = Assert.Single(impact.Tickets, ticket => ticket.Sla?.Breached == true);
        Assert.Equal(estate.VmApp, breached.CiId);
        Assert.Equal(0, breached.Sla!.RemainingSeconds);

        // The other ticket's priority matched no active policy, so it has no clock at all — a real
        // state, and one the panel has to be able to say rather than imply a deadline that never existed.
        Assert.Contains(impact.Tickets, ticket => ticket.CiId == estate.Service && ticket.Sla is null);
    }

    /// <summary>
    /// The panel names the CI each ticket is against, so an operator reading a list of six tickets can
    /// tell which of the affected machines each one is about without opening any of them.
    /// </summary>
    [Fact]
    public async Task Impact_NamesTheAffectedCiEachOpenTicketIsAgainst()
    {
        var estate = await BuildEstateAsync();

        var impact = await GetAsync<ImpactDto>($"/api/cis/{estate.Host}/impact");

        var ticket = Assert.Single(impact.Tickets, entry => entry.CiId == estate.VmApp);
        Assert.Contains("Finance ERP application server", ticket.CiName, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(impact.Cis, ci => ci.CiId == estate.VmApp).OpenTicketCount);
    }

    /// <summary>
    /// A resolved ticket is not exposure. It stays on the CI's history and leaves the blast radius,
    /// because what this panel answers is what is still to be dealt with.
    /// </summary>
    [Fact]
    public async Task Impact_AfterTheTicketIsResolved_DropsItFromTheRadius()
    {
        var estate = await BuildEstateAsync();
        await ResolveAsync(estate.ServiceTicket);

        var impact = await GetAsync<ImpactDto>($"/api/cis/{estate.Host}/impact");

        Assert.Equal(1, impact.Summary.OpenTicketCount);
        Assert.DoesNotContain(impact.Tickets, ticket => ticket.TicketId == estate.ServiceTicket);
    }

    /// <summary>
    /// Depth is the dial an operator reaches for when a radius is too wide to read. At one hop the
    /// service two hops out is not part of the answer — and neither is its ticket.
    /// </summary>
    [Fact]
    public async Task Impact_WithATighterDepth_StopsAtThatManyHopsAndSaysItWasReached()
    {
        var estate = await BuildEstateAsync();

        var impact = await GetAsync<ImpactDto>($"/api/cis/{estate.Host}/impact?maxDepth=1");

        Assert.Equal(3, impact.Summary.CiCount);
        Assert.DoesNotContain(impact.Cis, ci => ci.CiId == estate.Service);
        Assert.DoesNotContain(impact.Tickets, ticket => ticket.CiId == estate.Service);
        Assert.True(impact.MaxDepthReached);
    }

    /// <summary>
    /// The blast radius of a CI nothing depends on is the CI itself. Zero would be wrong — taking it
    /// away still takes it away — and an empty panel would read as a broken one.
    /// </summary>
    [Fact]
    public async Task Impact_ForACiNothingDependsOn_IsTheCiItselfAndNothingElse()
    {
        var lonely = await CreateCiAsync("Server", "Standalone jump box");

        var impact = await GetAsync<ImpactDto>($"/api/cis/{lonely}/impact");

        Assert.Equal(1, impact.Summary.CiCount);
        Assert.Equal(0, impact.Summary.DirectCiCount);
        Assert.Equal(lonely, Assert.Single(impact.Cis).CiId);
        Assert.Empty(impact.Tickets);
        Assert.Null(impact.Summary.NextSlaDueAt);
    }

    /// <summary>The failure path: a CI id nothing answers to is a 404 about the CI, not an empty radius.</summary>
    [Fact]
    public async Task Impact_ForACiThatDoesNotExist_Is404()
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{Guid.CreateVersion7()}/impact");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The CMDB is an agent surface (WP-2.1), and a blast radius names other people's assets, other
    /// people's tickets and the departments behind them.
    /// </summary>
    [Fact]
    public async Task Impact_AsEndUser_IsForbidden()
    {
        var lonely = await CreateCiAsync("Server", "Standalone jump box");

        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{lonely}/impact", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// One hypervisor host, two VMs racked on it, one business service behind one of those — the shape
    /// of the seeded estate's <c>dc1-esx-01</c>, which is what the WP's verification step walks.
    /// </summary>
    private async Task<Estate> BuildEstateAsync()
    {
        var host = await CreateCiAsync("Server", "DC1 hypervisor host");
        var vmApp = await CreateCiAsync("Virtual", "Finance ERP application server");
        var vmWeb = await CreateCiAsync("Virtual", "Customer portal web front end");
        var service = await CreateCiAsync("Logical", "Finance reporting service");

        // Source needs target (WP-2.3), so the VMs point at the host and the service points at the VM.
        await RelateAsync(vmApp, host, "RunsOn");
        await RelateAsync(vmWeb, host, "RunsOn");
        await RelateAsync(service, vmApp, "DependsOn");

        await SetOwnershipAsync(host, "IT", "Alex Doe");
        await SetOwnershipAsync(vmApp, "Finance", "Alex Doe");
        await SetOwnershipAsync(vmWeb, null, "Sam Roe");
        await SetOwnershipAsync(service, "Finance", "Sam Roe");

        var vmTicket = await CreateTicketAsync("ERP is unreachable");
        await LinkAsync(vmTicket, vmApp);
        await BreachSlaAsync(vmTicket);

        var serviceTicket = await CreateTicketAsync("Month end reporting is late");
        await LinkAsync(serviceTicket, service);
        await ClearSlaAsync(serviceTicket);

        return new(host, vmApp, vmWeb, service, vmTicket, serviceTicket);
    }

    private async Task<Guid> CreateCiAsync(string type, string name)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type,
            name = $"{name} {Guid.NewGuid():N}"[..48],
            attributes = AttributesFor(type),
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>()).Id;
    }

    private static Dictionary<string, string> AttributesFor(string type) => type switch
    {
        "Server" => new()
        {
            ["hostname"] = $"host-{Guid.NewGuid():N}"[..20],
            ["operatingSystem"] = "VMware ESXi 8.0",
            ["cpuCores"] = "32",
            ["ramGb"] = "512",
        },
        "Virtual" => new()
        {
            ["hostname"] = $"vm-{Guid.NewGuid():N}"[..20],
            ["hypervisor"] = "VMware ESXi 8.0",
            ["vcpuCores"] = "8",
            ["ramGb"] = "32",
        },
        "Logical" => new() { ["purpose"] = "Business service", ["serviceTier"] = "Gold" },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No fixture attributes for this CI type."),
    };

    private async Task RelateAsync(Guid sourceCiId, Guid targetCiId, string type)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/cis/{sourceCiId}/relationships");
        request.Content = JsonContent.Create(new { targetCiId, type });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// Written through the DbContext rather than the assignment endpoint: an owner and a department are
    /// Platform directory rows this suite's database may or may not have been seeded with, and what is
    /// under test is the roll-up rather than how the names got onto the CI.
    /// </summary>
    private async Task SetOwnershipAsync(Guid ciId, string? departmentName, string ownerName)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var ci = await dbContext.Cis.FirstAsync(entity => entity.Id == ciId);
        ci.OwnerUserId = DeterministicId(ownerName);
        ci.OwnerName = ownerName;
        ci.DepartmentId = departmentName is null ? null : DeterministicId(departmentName);
        ci.DepartmentName = departmentName;
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> CreateTicketAsync(string title)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/tickets");
        request.Content = JsonContent.Create(new
        {
            title,
            description = "Raised by the blast-radius integration test.",
            type = "Incident",
            urgency = "Medium",
            impact = "Medium",
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TicketDto>(await response.Content.ReadFromJsonAsync<TicketDto>()).Id;
    }

    private async Task LinkAsync(Guid ticketId, Guid ciId)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticketId}/cis");
        request.Content = JsonContent.Create(new { ciId });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// Walked through the real workflow rather than by writing the status column: nothing may reach
    /// Resolved without passing Triage, In Progress and Pending, and a test that skipped them would be
    /// asserting against a ticket state the platform cannot actually produce.
    /// </summary>
    private async Task ResolveAsync(Guid ticketId)
    {
        foreach (var status in new[] { "Triage", "InProgress", "Pending", "Resolved" })
        {
            using var request = Authenticated(HttpMethod.Post, $"/api/tickets/{ticketId}/transitions");
            request.Content = JsonContent.Create(new
            {
                targetStatus = status,
                // Required when resolving, and harmless on the way there.
                resolutionNote = "Closed by the blast-radius integration test.",
            });
            using var response = await _client!.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// Puts one ticket's resolution clock past its target.
    /// <para>
    /// The policy is written here rather than through <c>POST /api/sla/policies</c>, and written
    /// <em>inactive</em>, quite deliberately: an active policy is picked up by
    /// <c>SlaService.StartAsync</c> for every ticket of that priority raised anywhere, and this suite
    /// shares its database with every other class. An inactive policy is attached to this ticket alone
    /// and reaches nobody else's — and the exposure arithmetic never reads the flag.
    /// </para>
    /// </summary>
    private async Task BreachSlaAsync(Guid ticketId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();

        var calendar = new BusinessHoursCalendar
        {
            Id = Guid.CreateVersion7(),
            Name = $"Impact test calendar {Guid.NewGuid():N}"[..40],
            TimeZoneId = "UTC",
            // Around the clock, so the assertion is about the exposure rather than about what time the
            // suite happened to run at.
            WorkingDays = BusinessDays.Weekdays | BusinessDays.Saturday | BusinessDays.Sunday,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var policy = new SlaPolicy
        {
            Id = Guid.CreateVersion7(),
            Name = "Impact test policy",
            Priority = TicketPriority.Critical,
            ResponseTargetMinutes = 30,
            ResolutionTargetMinutes = 240,
            WarningPercent = 80,
            CalendarId = calendar.Id,
            IsActive = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.BusinessHoursCalendars.Add(calendar);
        dbContext.SlaPolicies.Add(policy);

        var existing = await dbContext.TicketSlas.FirstOrDefaultAsync(sla => sla.TicketId == ticketId);
        if (existing is null)
        {
            dbContext.TicketSlas.Add(new TicketSla
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticketId,
                PolicyId = policy.Id,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-9),
                ActiveSince = null,
                AccumulatedBusinessSeconds = TimeSpan.FromHours(9).TotalSeconds,
            });
        }
        else
        {
            existing.PolicyId = policy.Id;
            existing.ActiveSince = null;
            existing.AccumulatedBusinessSeconds = TimeSpan.FromHours(9).TotalSeconds;
            existing.ResolutionCompletedAt = null;
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Leaves a ticket with no SLA at all — the state of any ticket whose priority matched no active
    /// policy when it was raised, which on a fresh database is most of them.
    /// </summary>
    private async Task ClearSlaAsync(Guid ticketId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await dbContext.TicketSlas.Where(sla => sla.TicketId == ticketId).ExecuteDeleteAsync();
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
        request.Headers.Add(ImpactAuthenticationHandler.RoleHeader, role);
        return request;
    }

    /// <summary>One id per name, so two CIs owned by "Alex Doe" roll up to one person.</summary>
    private static Guid DeterministicId(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes);
    }

    private sealed record Estate(
        Guid Host, Guid VmApp, Guid VmWeb, Guid Service, Guid VmTicket, Guid ServiceTicket);

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record TicketDto(Guid Id, string Number, string Title);

    private sealed record SlaExposureDto(
        string PolicyName, DateTimeOffset ResolutionDueAt, double RemainingSeconds, bool Breached, bool AtRisk);

    private sealed record ImpactedCiDto(
        Guid CiId, string Name, string Type, string LifecycleState, bool IsActive, int Depth,
        Guid? OwnerUserId, string? OwnerName, Guid? DepartmentId, string? DepartmentName,
        string? SiteName, int OpenTicketCount);

    private sealed record ImpactedTicketDto(
        Guid TicketId, string Number, string Title, string Status, string Priority,
        DateTimeOffset CreatedAt, Guid CiId, string CiName, SlaExposureDto? Sla);

    private sealed record ImpactedDepartmentDto(Guid DepartmentId, string Name, int CiCount, int OpenTicketCount);

    private sealed record ImpactedUserDto(Guid UserId, string Name, int CiCount, int OpenTicketCount);

    private sealed record ImpactSummaryDto(
        int CiCount, int DirectCiCount, int OpenTicketCount, int BreachedSlaCount, int AtRiskSlaCount,
        DateTimeOffset? NextSlaDueAt, int AffectedUserCount, int AffectedDepartmentCount,
        int CisWithoutDepartment, bool CisTruncated, bool TicketsTruncated);

    private sealed record ImpactDto(
        Guid RootCiId, string RootCiName, string RootCiType, int MaxDepth, bool MaxDepthReached,
        bool ContainsCycle, ImpactSummaryDto Summary, List<ImpactedCiDto> Cis,
        List<ImpactedTicketDto> Tickets, List<ImpactedDepartmentDto> Departments, List<ImpactedUserDto> Users);

    private sealed class ImpactApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public ImpactApplication(
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
                    // Creating a CI and creating a ticket both publish through the outbox, so the bus has
                    // to be configured even though nothing here reads a message. Every hosted service is
                    // removed below, so no sweeper of this host's competes with another suite's.
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ImpactAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = ImpactAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = ImpactAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ImpactAuthenticationHandler>(
                        ImpactAuthenticationHandler.TestScheme,
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

    private sealed class ImpactAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "ImpactTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "impact-test-subject"),
                    new Claim("sub", "impact-test-subject"),
                    new Claim("name", "Impact Test"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
