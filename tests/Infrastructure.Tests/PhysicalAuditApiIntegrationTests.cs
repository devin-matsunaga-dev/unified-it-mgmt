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
/// The physical audit end to end: open a count over a site, confirm assets by the codes on their
/// labels, and read back what did not turn up.
/// <para>
/// Every session here is scoped to a site of its own. The report is a statement about everything in
/// scope and the database is shared by the whole suite, so an estate-wide session would count every CI
/// every other class has ever created — the shared-table trap in its "asserting something about all of
/// it" shape.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PhysicalAuditApiIntegrationTests : IAsyncLifetime
{
    private readonly AuditApplication _application;
    private HttpClient? _client;

    public PhysicalAuditApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        _application = new AuditApplication(
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
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _application.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The WP's own verification step: three of five confirmed, and the report lists the rest.</summary>
    [Fact]
    public async Task Audit_WithThreeOfFiveConfirmed_ListsTheTwoThatDidNotTurnUp()
    {
        var site = Guid.CreateVersion7();
        var estate = await CreateEstateAsync(site, count: 5);
        var session = await OpenSessionAsync(site);

        foreach (var ci in estate.Take(3))
        {
            var scan = await ScanAsync(session.Id, ci.AssetTag);
            Assert.True(scan.Expected);
            Assert.False(scan.AlreadyScanned);
        }

        var report = await GetAsync<ReportDto>($"/api/audit-sessions/{session.Id}/report");

        Assert.Equal(5, report.Session.ExpectedCount);
        Assert.Equal(3, report.Session.ScannedCount);
        Assert.Equal(2, report.Session.UnscannedCount);
        Assert.Equal(
            estate.Skip(3).Select(ci => ci.Id).Order(),
            report.Unscanned.Select(item => item.CiId).Order());
        Assert.Empty(report.Unexpected);
    }

    /// <summary>
    /// A serial number is as good as an asset tag, because WP-2.7's one resolver answers both — a stock
    /// take and a lookup must never disagree about which asset a sticker names.
    /// </summary>
    [Fact]
    public async Task Audit_ScannedBySerialNumber_ConfirmsTheSameAsset()
    {
        var site = Guid.CreateVersion7();
        var ci = (await CreateEstateAsync(site, count: 1)).Single();
        var session = await OpenSessionAsync(site);

        var scan = await ScanAsync(session.Id, ci.SerialNumber);

        Assert.Equal(ci.Id, scan.CiId);
    }

    /// <summary>
    /// Two people walking one rack is the normal case. Refusing the second scan would tell the second
    /// one the asset was missing, which is the single worst answer this workflow can give.
    /// </summary>
    [Fact]
    public async Task Audit_ScannedTwiceInOneSession_IsCountedOnceAndSaysSo()
    {
        var site = Guid.CreateVersion7();
        var ci = (await CreateEstateAsync(site, count: 1)).Single();
        var session = await OpenSessionAsync(site);

        var first = await ScanAsync(session.Id, ci.AssetTag);
        var again = await ScanAsync(session.Id, ci.AssetTag, expectCreated: false);

        Assert.False(first.AlreadyScanned);
        Assert.True(again.AlreadyScanned);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(1, (await GetAsync<SessionDto>($"/api/audit-sessions/{session.Id}")).ScannedCount);
    }

    /// <summary>
    /// The finding a count that only reported absences would miss: an asset in this rack that the CMDB
    /// records at another site. Confirmed, and reported as not belonging here.
    /// </summary>
    [Fact]
    public async Task Audit_WhenSomethingRecordedElsewhereIsScanned_IsReportedAsUnexpected()
    {
        var site = Guid.CreateVersion7();
        var stranger = (await CreateEstateAsync(Guid.CreateVersion7(), count: 1)).Single();
        var session = await OpenSessionAsync(site);

        var scan = await ScanAsync(session.Id, stranger.AssetTag);
        var report = await GetAsync<ReportDto>($"/api/audit-sessions/{session.Id}/report");

        Assert.False(scan.Expected);
        Assert.Equal("DifferentSite", scan.UnexpectedReason);
        var unexpected = Assert.Single(report.Unexpected);
        Assert.Equal(stranger.Id, unexpected.CiId);
        Assert.Equal("DifferentSite", unexpected.Reason);
        Assert.Equal(0, report.Session.ScannedCount);
    }

    /// <summary>An undo has to exist, and it moves an asset from found back to missing.</summary>
    [Fact]
    public async Task Audit_WhenAScanIsRemoved_TheAssetGoesBackOnTheUnscannedList()
    {
        var site = Guid.CreateVersion7();
        var ci = (await CreateEstateAsync(site, count: 1)).Single();
        var session = await OpenSessionAsync(site);
        var scan = await ScanAsync(session.Id, ci.AssetTag);

        using var request = Authenticated(HttpMethod.Delete, $"/api/audit-sessions/{session.Id}/scans/{scan.Id}");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var report = await GetAsync<ReportDto>($"/api/audit-sessions/{session.Id}/report");
        Assert.Equal(ci.Id, Assert.Single(report.Unscanned).CiId);
    }

    [Fact]
    public async Task Audit_Closed_KeepsItsFindingsAndIsAudited()
    {
        var site = Guid.CreateVersion7();
        var estate = await CreateEstateAsync(site, count: 2);
        var session = await OpenSessionAsync(site);
        await ScanAsync(session.Id, estate[0].AssetTag);

        var closed = await PostAsync<SessionDto>($"/api/audit-sessions/{session.Id}/closure", new { note = "Counted by hand." });

        Assert.Equal("Closed", closed.Status);
        Assert.Equal("audit-test-user", closed.ClosedBy);
        Assert.Equal(1, closed.UnscannedCount);

        await using var scope = _application.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entityId = session.Id.ToString();
        var entries = await platform.AuditEntries
            .Where(entry => entry.EntityType == "PhysicalAuditSession" && entry.EntityId == entityId)
            .ToListAsync();
        Assert.Contains(entries, entry => entry.Action == "Opened");
        Assert.Contains(entries, entry => entry.Action == "Scanned");
        Assert.Contains(entries, entry => entry.Action == "Closed");
    }

    /// <summary>
    /// Failure path: a count somebody can top up next week counted nothing on the day, so a closed
    /// session refuses a scan rather than quietly accepting it.
    /// </summary>
    [Fact]
    public async Task Audit_ScannedIntoAClosedSession_IsAConflict()
    {
        var site = Guid.CreateVersion7();
        var ci = (await CreateEstateAsync(site, count: 1)).Single();
        var session = await OpenSessionAsync(site);
        await PostAsync<SessionDto>($"/api/audit-sessions/{session.Id}/closure", new { });

        using var request = Authenticated(HttpMethod.Post, $"/api/audit-sessions/{session.Id}/scans");
        request.Content = JsonContent.Create(new { code = ci.AssetTag });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Audit_ClosedTwice_IsAConflictNamingWhoClosedIt()
    {
        var session = await OpenSessionAsync(Guid.CreateVersion7());
        await PostAsync<SessionDto>($"/api/audit-sessions/{session.Id}/closure", new { });

        using var request = Authenticated(HttpMethod.Post, $"/api/audit-sessions/{session.Id}/closure");
        request.Content = JsonContent.Create(new { });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("audit-test-user", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Failure path: a code nothing answers to is a 404 about the code, not about the session.</summary>
    [Fact]
    public async Task Audit_ScannedWithACodeNothingAnswersTo_IsNotFoundNamingTheCode()
    {
        var session = await OpenSessionAsync(Guid.CreateVersion7());
        var code = $"NOT-A-TAG-{Guid.NewGuid():N}"[..20];

        using var request = Authenticated(HttpMethod.Post, $"/api/audit-sessions/{session.Id}/scans");
        request.Content = JsonContent.Create(new { code });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(code, problem, StringComparison.Ordinal);
    }

    /// <summary>Failure path: a count with no name cannot be told apart from the last one.</summary>
    [Fact]
    public async Task Audit_OpenedWithoutAName_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/audit-sessions");
        request.Content = JsonContent.Create(new { name = "   " });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Name", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Failure path: a site the directory does not hold is a field error rather than a scope of nothing.</summary>
    [Fact]
    public async Task Audit_OpenedAgainstASiteThatDoesNotExist_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/audit-sessions");
        request.Content = JsonContent.Create(new { name = $"Ghost site count {Guid.NewGuid():N}", siteId = Guid.CreateVersion7() });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("siteId", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_ThatDoesNotExist_IsNotFoundOnEveryVerb()
    {
        var missing = Guid.CreateVersion7();

        using var read = Authenticated(HttpMethod.Get, $"/api/audit-sessions/{missing}");
        using var readResponse = await _client!.SendAsync(read);
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        using var report = Authenticated(HttpMethod.Get, $"/api/audit-sessions/{missing}/report");
        using var reportResponse = await _client!.SendAsync(report);
        Assert.Equal(HttpStatusCode.NotFound, reportResponse.StatusCode);

        using var scan = Authenticated(HttpMethod.Post, $"/api/audit-sessions/{missing}/scans");
        scan.Content = JsonContent.Create(new { code = "ANYTHING" });
        using var scanResponse = await _client!.SendAsync(scan);
        Assert.Equal(HttpStatusCode.NotFound, scanResponse.StatusCode);
    }

    [Fact]
    public async Task Audit_AsEndUser_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/audit-sessions", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Audit_Listed_CarriesItsScanCountAndCanBeFilteredByStatus()
    {
        var site = Guid.CreateVersion7();
        var ci = (await CreateEstateAsync(site, count: 1)).Single();
        var open = await OpenSessionAsync(site);
        await ScanAsync(open.Id, ci.AssetTag);
        var shut = await OpenSessionAsync(site);
        await PostAsync<SessionDto>($"/api/audit-sessions/{shut.Id}/closure", new { });

        var openOnly = await GetAsync<SessionPageDto>("/api/audit-sessions?status=Open&pageSize=200");
        var closedOnly = await GetAsync<SessionPageDto>("/api/audit-sessions?status=Closed&pageSize=200");

        Assert.Equal(1, Assert.Single(openOnly.Items, item => item.Id == open.Id).ScanCount);
        Assert.DoesNotContain(closedOnly.Items, item => item.Id == open.Id);
        Assert.Contains(closedOnly.Items, item => item.Id == shut.Id);
    }

    /// <summary>
    /// A CI leaving the estate takes its scans with it — an install is a property of one machine
    /// (WP-4.4), and so is the evidence that somebody found it.
    /// </summary>
    [Fact]
    public async Task Audit_WhenAScannedCiIsDeleted_LosesThatScanAndKeepsTheSession()
    {
        var site = Guid.CreateVersion7();
        var estate = await CreateEstateAsync(site, count: 2);
        var session = await OpenSessionAsync(site);
        await ScanAsync(session.Id, estate[0].AssetTag);
        await ScanAsync(session.Id, estate[1].AssetTag);

        using var delete = Authenticated(HttpMethod.Delete, $"/api/cis/{estate[0].Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var report = await GetAsync<ReportDto>($"/api/audit-sessions/{session.Id}/report");
        Assert.Equal(estate[1].Id, Assert.Single(report.Scanned).CiId);
        Assert.Equal(1, report.Session.ExpectedCount);
    }

    /// <summary>
    /// Every session opened here is pinned to a site nothing else writes to, so the count is over this
    /// class's own estate rather than over the whole shared database.
    /// </summary>
    private async Task<SessionDto> OpenSessionAsync(Guid siteId)
    {
        // The site has to exist in the platform directory for the endpoint to accept it, and this
        // suite's database may not have been seeded — so the row is written here, named after the test.
        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // Two counts of one site is a normal thing to test, so the row is adopted rather than
            // inserted twice.
            if (!await platform.Sites.AnyAsync(site => site.Id == siteId))
            {
                // The tail of the id rather than its head: a v7 GUID starts with a timestamp, so eight
                // ids minted in one test run share their first hex characters and collide on the
                // unique site code. Found by this class failing as a group and passing alone.
                platform.Sites.Add(new Site
                {
                    Id = siteId,
                    Code = $"AUD{SiteSuffix(siteId)}".ToUpperInvariant(),
                    Name = SiteNameFor(siteId),
                });
                await platform.SaveChangesAsync();
            }
        }

        return await PostAsync<SessionDto>("/api/audit-sessions", new
        {
            name = $"Count {Guid.NewGuid():N}"[..20],
            siteId,
        });
    }

    /// <summary>Physical assets at one site, each with the asset tag and serial a label carries.</summary>
    private async Task<IReadOnlyList<CiDto>> CreateEstateAsync(Guid siteId, int count)
    {
        var cis = new List<CiDto>(count);
        for (var index = 0; index < count; index++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            using var request = Authenticated(HttpMethod.Post, "/api/cis");
            request.Content = JsonContent.Create(new
            {
                type = "NetworkDevice",
                name = $"audit-sw-{suffix}",
                assetTag = $"AUD-{suffix}",
                serialNumber = $"SER-{suffix}",
                attributes = new Dictionary<string, string>
                {
                    ["managementIp"] = $"10.{Random.Shared.Next(100, 250)}.{Random.Shared.Next(1, 250)}.{index + 1}",
                    ["vendor"] = "Cisco",
                    ["portCount"] = "24",
                },
            });
            using var response = await _client!.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var ci = Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());

            await using var scope = _application.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
            var stored = await dbContext.Cis.FirstAsync(entity => entity.Id == ci.Id);
            stored.SiteId = siteId;
            stored.SiteName = SiteNameFor(siteId);
            await dbContext.SaveChangesAsync();

            cis.Add(ci);
        }

        return cis;
    }

    private static string SiteSuffix(Guid siteId) => siteId.ToString("N")[^10..];

    private static string SiteNameFor(Guid siteId) => $"Audit test site {SiteSuffix(siteId)}";

    private async Task<ScanDto> ScanAsync(Guid sessionId, string? code, bool expectCreated = true)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/audit-sessions/{sessionId}/scans");
        request.Content = JsonContent.Create(new { code });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(expectCreated ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ScanDto>(await response.Content.ReadFromJsonAsync<ScanDto>());
    }

    private async Task<T> PostAsync<T>(string uri, object body)
    {
        using var request = Authenticated(HttpMethod.Post, uri);
        request.Content = JsonContent.Create(body);
        using var response = await _client!.SendAsync(request);
        Assert.Contains(response.StatusCode, (HttpStatusCode[])[HttpStatusCode.Created, HttpStatusCode.OK]);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
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
        request.Headers.Add(AuditAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name, string? AssetTag, string? SerialNumber);

    private sealed record SessionDto(
        Guid Id,
        string Name,
        Guid? SiteId,
        string? SiteName,
        string Status,
        string OpenedBy,
        DateTimeOffset OpenedAt,
        string? ClosedBy,
        DateTimeOffset? ClosedAt,
        string? Note,
        int ExpectedCount,
        int ScannedCount,
        int UnscannedCount,
        int UnexpectedCount);

    private sealed record SessionSummaryDto(Guid Id, string Name, string Status, int ScanCount);

    private sealed record SessionPageDto(List<SessionSummaryDto> Items, int Total, int Page, int PageSize);

    private sealed record ItemDto(
        Guid CiId,
        string Name,
        string Type,
        string? AssetTag,
        string? SerialNumber,
        string LifecycleState,
        string? SiteName,
        string? OwnerName,
        DateTimeOffset? ScannedAt,
        string? ScannedBy);

    private sealed record UnexpectedDto(Guid CiId, string Name, string Reason, DateTimeOffset ScannedAt);

    private sealed record ReportDto(
        SessionDto Session,
        List<ItemDto> Scanned,
        List<ItemDto> Unscanned,
        List<UnexpectedDto> Unexpected,
        bool Truncated,
        DateTimeOffset GeneratedAt);

    private sealed record ScanDto(
        Guid Id,
        Guid SessionId,
        Guid CiId,
        string CiName,
        string CiType,
        string? AssetTag,
        string? SerialNumber,
        string Code,
        string ScannedBy,
        DateTimeOffset ScannedAt,
        string? Note,
        bool AlreadyScanned,
        bool Expected,
        string? UnexpectedReason);

    private sealed class AuditApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public AuditApplication(
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
                    // Creating and deleting a CI publishes through the outbox. Every hosted service is
                    // removed below, so no sweeper of this host's competes with another suite's.
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = AuditAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = AuditAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = AuditAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, AuditAuthenticationHandler>(
                        AuditAuthenticationHandler.TestScheme,
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

    private sealed class AuditAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "AuditTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "audit-test-user-id"),
                    new Claim(ClaimTypes.Name, "audit-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
