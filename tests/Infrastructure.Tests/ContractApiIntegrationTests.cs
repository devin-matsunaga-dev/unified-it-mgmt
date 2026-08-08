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
using Modules.Assets.Features.Contracts;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// WP-2.6 end to end: vendors and contracts through the API, warranty dates on a CI, the contract's
/// covered-CI list, and the expiry pass raising exactly one notification per crossed threshold.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ContractApiIntegrationTests : IAsyncLifetime
{
    private readonly ContractApplication _application;
    private HttpClient? _client;

    public ContractApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new ContractApplication(
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

    [Fact]
    public async Task CreateContract_ReturnsItsVendorStatusAndDaysRemaining()
    {
        var vendor = await CreateVendorAsync();
        var endDate = Today.AddDays(45);

        var contract = await CreateContractAsync(vendor.Id, endDate: endDate);

        Assert.Equal(vendor.Name, contract.VendorName);
        Assert.Equal("Active", contract.Status);
        Assert.Equal(45, contract.DaysRemaining);
        Assert.Equal(0, contract.CoveredCiCount);
        Assert.True(contract.IsActive);
    }

    [Fact]
    public async Task Contracts_AreAudited()
    {
        var vendor = await CreateVendorAsync();
        var contract = await CreateContractAsync(vendor.Id);

        await using var scope = _application.Services.CreateAsyncScope();
        var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entityId = contract.Id.ToString();
        var entry = await platformContext.AuditEntries
            .SingleAsync(audit => audit.EntityType == "Contract" && audit.EntityId == entityId
                && audit.Action == "Created");

        Assert.Equal("contract-test-user-id", entry.ActorId);
        Assert.Contains(
            await platformContext.AuditEntries.ToListAsync(),
            audit => audit.EntityType == "Vendor" && audit.EntityId == vendor.Id.ToString()
                && audit.Action == "Created");
    }

    /// <summary>The WP's second verification step: a contract page lists the CIs it covers.</summary>
    [Fact]
    public async Task ContractCoverage_ListsEveryCoveredCi()
    {
        var vendor = await CreateVendorAsync();
        var contract = await CreateContractAsync(vendor.Id);
        var covered = await CreateCiAsync("Server", "Covered server");
        var other = await CreateCiAsync("Server", "Uncovered server");

        var updated = await SetCoverageAsync(covered.Id, new
        {
            contractId = contract.Id,
            purchaseDate = Today.AddYears(-1).ToString("yyyy-MM-dd"),
            warrantyExpiresAt = Today.AddDays(60).ToString("yyyy-MM-dd"),
        });

        Assert.Equal(contract.Id, updated.Coverage.ContractId);
        Assert.Equal(contract.ContractNumber, updated.Coverage.ContractNumber);
        Assert.Equal(vendor.Name, updated.Coverage.VendorName);
        Assert.Equal("Active", updated.Coverage.WarrantyStatus);
        Assert.Equal(60, updated.Coverage.WarrantyDaysRemaining);

        var page = await GetAsync<CiPageDto>($"/api/cis?contractId={contract.Id}");
        var listed = Assert.Single(page.Items);
        Assert.Equal(covered.Id, listed.Id);
        Assert.DoesNotContain(page.Items, ci => ci.Id == other.Id);

        var reread = await GetAsync<ContractDto>($"/api/contracts/{contract.Id}");
        Assert.Equal(1, reread.CoveredCiCount);
    }

    /// <summary>Coverage is a complete statement, so an empty body releases the CI.</summary>
    [Fact]
    public async Task SetCoverage_WithNoContract_ReleasesTheCi()
    {
        var vendor = await CreateVendorAsync();
        var contract = await CreateContractAsync(vendor.Id);
        var ci = await CreateCiAsync("Server", "Server that changes its mind");
        await SetCoverageAsync(ci.Id, new { contractId = contract.Id });

        var released = await SetCoverageAsync(ci.Id, new { });

        Assert.Null(released.Coverage.ContractId);
        Assert.Null(released.Coverage.WarrantyExpiresAt);
        Assert.Empty((await GetAsync<CiPageDto>($"/api/cis?contractId={contract.Id}")).Items);
    }

    /// <summary>
    /// The WP's first verification step: a warranty expiring tomorrow produces a notification on the
    /// next run of the job — and only one, however many times the job runs.
    /// </summary>
    [Fact]
    public async Task ExpiryRun_ForAWarrantyExpiringTomorrow_RaisesOneNotificationAndRepeatsSilently()
    {
        var ci = await CreateCiAsync("Hardware", "Laptop out of warranty tomorrow");
        await SetCoverageAsync(ci.Id, new { warrantyExpiresAt = Today.AddDays(1).ToString("yyyy-MM-dd") });

        var run = await RunExpiryAsync();

        var notice = Assert.Single(run.Raised, raised => raised.SubjectId == ci.Id);
        Assert.Equal("Warranty", notice.Subject);
        Assert.Equal(7, notice.ThresholdDays);
        Assert.Contains("expires in 1 days", notice.Message, StringComparison.Ordinal);
        Assert.Contains(ci.Name, notice.SubjectName, StringComparison.Ordinal);

        var second = await RunExpiryAsync();
        Assert.DoesNotContain(second.Raised, raised => raised.SubjectId == ci.Id);

        var recorded = await GetAsync<List<NotificationDto>>("/api/contract-notifications?limit=200");
        Assert.Single(recorded, entry => entry.SubjectId == ci.Id);
    }

    /// <summary>Nobody renews the warranty on an asset that has left the estate.</summary>
    [Fact]
    public async Task ExpiryRun_ForARetiredCi_RaisesNothing()
    {
        var ci = await CreateCiAsync("Hardware", "Laptop on its way out");
        await SetCoverageAsync(ci.Id, new { warrantyExpiresAt = Today.AddDays(3).ToString("yyyy-MM-dd") });
        foreach (var state in (string[])["Deployed", "Retired"])
        {
            using var transition = Authenticated(HttpMethod.Post, $"/api/cis/{ci.Id}/lifecycle-transitions");
            transition.Content = JsonContent.Create(new { targetState = state });
            using var moved = await _client!.SendAsync(transition);
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        var run = await RunExpiryAsync();

        Assert.DoesNotContain(run.Raised, raised => raised.SubjectId == ci.Id);
    }

    [Fact]
    public async Task ExpiryRun_ForAContractInsideTheThirtyDayWindow_RaisesTheContractNotice()
    {
        var vendor = await CreateVendorAsync();
        var contract = await CreateContractAsync(vendor.Id, endDate: Today.AddDays(30));

        var run = await RunExpiryAsync();

        var notice = Assert.Single(run.Raised, raised => raised.SubjectId == contract.Id);
        Assert.Equal("Contract", notice.Subject);
        Assert.Equal(30, notice.ThresholdDays);
        Assert.Contains(contract.ContractNumber, notice.SubjectName, StringComparison.Ordinal);
        Assert.Equal(ContractExpiryService.DefaultFallbackRecipient, notice.Recipient);
    }

    /// <summary>A contract still a long way off is not anybody's problem yet.</summary>
    [Fact]
    public async Task ExpiryRun_ForAContractBeyondThirtyDays_RaisesNothing()
    {
        var vendor = await CreateVendorAsync();
        var contract = await CreateContractAsync(vendor.Id, endDate: Today.AddDays(31));

        var run = await RunExpiryAsync();

        Assert.DoesNotContain(run.Raised, raised => raised.SubjectId == contract.Id);
    }

    /// <summary>Renewing moves the end date, which must start a fresh 30/7/0 cycle.</summary>
    [Fact]
    public async Task ExpiryRun_AfterARenewalMovesTheEndDate_RaisesAgainForTheNewDate()
    {
        var vendor = await CreateVendorAsync();
        var contract = await CreateContractAsync(vendor.Id, endDate: Today.AddDays(30));
        await RunExpiryAsync();

        using var renewal = Authenticated(HttpMethod.Put, $"/api/contracts/{contract.Id}");
        renewal.Content = JsonContent.Create(new
        {
            vendorId = vendor.Id,
            contractNumber = contract.ContractNumber,
            name = contract.Name,
            type = "Support",
            startDate = Today.AddYears(-1).ToString("yyyy-MM-dd"),
            endDate = Today.AddDays(29).ToString("yyyy-MM-dd"),
            isActive = true,
        });
        using var renewed = await _client!.SendAsync(renewal);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        var run = await RunExpiryAsync();

        var notice = Assert.Single(run.Raised, raised => raised.SubjectId == contract.Id);
        Assert.Equal(30, notice.ThresholdDays);
        Assert.Equal(Today.AddDays(29).ToString("yyyy-MM-dd"), notice.DueDate);
    }

    /// <summary>Failure path: a contract cannot end before it starts.</summary>
    [Fact]
    public async Task CreateContract_WithEndDateBeforeStartDate_ReturnsValidationProblem()
    {
        var vendor = await CreateVendorAsync();

        using var request = Authenticated(HttpMethod.Post, "/api/contracts");
        request.Content = JsonContent.Create(new
        {
            vendorId = vendor.Id,
            contractNumber = $"C-{Guid.NewGuid():N}"[..12],
            name = "Backwards contract",
            type = "Support",
            startDate = Today.ToString("yyyy-MM-dd"),
            endDate = Today.AddDays(-1).ToString("yyyy-MM-dd"),
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("end date cannot be before the start date", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateContract_ForAnUnknownVendor_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/contracts");
        request.Content = JsonContent.Create(new
        {
            vendorId = Guid.CreateVersion7(),
            contractNumber = $"C-{Guid.NewGuid():N}"[..12],
            name = "Contract with no vendor",
            type = "Support",
            startDate = Today.ToString("yyyy-MM-dd"),
            endDate = Today.AddDays(30).ToString("yyyy-MM-dd"),
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateContract_WithADuplicateNumber_ReturnsConflict()
    {
        var vendor = await CreateVendorAsync();
        var contract = await CreateContractAsync(vendor.Id);

        using var request = Authenticated(HttpMethod.Post, "/api/contracts");
        request.Content = JsonContent.Create(new
        {
            vendorId = vendor.Id,
            contractNumber = contract.ContractNumber.ToLowerInvariant(),
            name = "Second contract, same number",
            type = "Support",
            startDate = Today.ToString("yyyy-MM-dd"),
            endDate = Today.AddDays(30).ToString("yyyy-MM-dd"),
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already used", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateVendor_Twice_ReturnsConflict()
    {
        var vendor = await CreateVendorAsync();

        using var request = Authenticated(HttpMethod.Post, "/api/vendors");
        request.Content = JsonContent.Create(new { name = vendor.Name.ToUpperInvariant() });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteContract_WhileItStillCoversACi_ReturnsConflict()
    {
        var vendor = await CreateVendorAsync();
        var contract = await CreateContractAsync(vendor.Id);
        var ci = await CreateCiAsync("Server", "Server under contract");
        await SetCoverageAsync(ci.Id, new { contractId = contract.Id });

        using var blocked = Authenticated(HttpMethod.Delete, $"/api/contracts/{contract.Id}");
        using var blockedResponse = await _client!.SendAsync(blocked);
        var problem = await blockedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, blockedResponse.StatusCode);
        Assert.Contains("Release the CIs", problem, StringComparison.Ordinal);

        await SetCoverageAsync(ci.Id, new { });

        using var allowed = Authenticated(HttpMethod.Delete, $"/api/contracts/{contract.Id}");
        using var allowedResponse = await _client!.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteVendor_WhileItStillHasContracts_ReturnsConflict()
    {
        var vendor = await CreateVendorAsync();
        await CreateContractAsync(vendor.Id);

        using var response = await _client!.SendAsync(Authenticated(HttpMethod.Delete, $"/api/vendors/{vendor.Id}"));
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("contracts before deleting it", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetCoverage_OnADisposedCi_ReturnsConflict()
    {
        var ci = await CreateCiAsync("Server", "Server that left the estate");
        foreach (var state in (string[])["Deployed", "Retired", "Disposed"])
        {
            using var transition = Authenticated(HttpMethod.Post, $"/api/cis/{ci.Id}/lifecycle-transitions");
            transition.Content = JsonContent.Create(new { targetState = state });
            using var moved = await _client!.SendAsync(transition);
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ci.Id}/coverage");
        request.Content = JsonContent.Create(new { warrantyExpiresAt = Today.AddDays(10).ToString("yyyy-MM-dd") });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("disposed CI's coverage", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetCoverage_WithAnUnknownContract_ReturnsValidationProblem()
    {
        var ci = await CreateCiAsync("Server", "Server pointing at nothing");

        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ci.Id}/coverage");
        request.Content = JsonContent.Create(new { contractId = Guid.CreateVersion7() });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetCoverage_WithAWarrantyEndingBeforeThePurchase_ReturnsValidationProblem()
    {
        var ci = await CreateCiAsync("Server", "Server bought after its warranty");

        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ci.Id}/coverage");
        request.Content = JsonContent.Create(new
        {
            purchaseDate = Today.ToString("yyyy-MM-dd"),
            warrantyExpiresAt = Today.AddDays(-1).ToString("yyyy-MM-dd"),
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot end before the asset was bought", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Contracts_FilterByStatusAndSearchOnNumberAndVendor()
    {
        var vendor = await CreateVendorAsync();
        var expiring = await CreateContractAsync(vendor.Id, endDate: Today.AddDays(10));
        var distant = await CreateContractAsync(vendor.Id, endDate: Today.AddDays(400));

        var soon = await GetAsync<ContractPageDto>("/api/contracts?status=ExpiringSoon&pageSize=200");
        Assert.Contains(soon.Items, item => item.Id == expiring.Id);
        Assert.DoesNotContain(soon.Items, item => item.Id == distant.Id);

        var byNumber = await GetAsync<ContractPageDto>($"/api/contracts?search={distant.ContractNumber}");
        Assert.Equal(distant.Id, Assert.Single(byNumber.Items).Id);

        var byVendor = await GetAsync<ContractPageDto>($"/api/contracts?search={vendor.Name}&pageSize=200");
        Assert.Equal(2, byVendor.Items.Count(item => item.VendorName == vendor.Name));
    }

    [Fact]
    public async Task Cis_FilterByWarrantyWindow()
    {
        var soon = await CreateCiAsync("Hardware", "Laptop nearly out of warranty");
        var later = await CreateCiAsync("Hardware", "Laptop with years left");
        await SetCoverageAsync(soon.Id, new { warrantyExpiresAt = Today.AddDays(5).ToString("yyyy-MM-dd") });
        await SetCoverageAsync(later.Id, new { warrantyExpiresAt = Today.AddDays(500).ToString("yyyy-MM-dd") });

        var page = await GetAsync<CiPageDto>("/api/cis?warrantyExpiringWithinDays=30&pageSize=200");

        Assert.Contains(page.Items, ci => ci.Id == soon.Id);
        Assert.DoesNotContain(page.Items, ci => ci.Id == later.Id);
    }

    [Fact]
    public async Task Contracts_AsEndUser_AreForbidden()
    {
        using var response = await _client!.SendAsync(Authenticated(HttpMethod.Get, "/api/contracts", "EndUser"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<VendorDto> CreateVendorAsync()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/vendors");
        request.Content = JsonContent.Create(new
        {
            name = $"Vendor {Guid.NewGuid():N}",
            contactEmail = "support@vendor.example.test",
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<VendorDto>(await response.Content.ReadFromJsonAsync<VendorDto>());
    }

    private async Task<ContractDto> CreateContractAsync(Guid vendorId, DateOnly? endDate = null)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/contracts");
        request.Content = JsonContent.Create(new
        {
            vendorId,
            contractNumber = $"C-{Guid.NewGuid():N}"[..14],
            name = "ProSupport",
            type = "Support",
            startDate = Today.AddYears(-1).ToString("yyyy-MM-dd"),
            endDate = (endDate ?? Today.AddDays(45)).ToString("yyyy-MM-dd"),
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<ContractDto>(await response.Content.ReadFromJsonAsync<ContractDto>());
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

    private async Task<CiDto> SetCoverageAsync(Guid ciId, object body)
    {
        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{ciId}/coverage");
        request.Content = JsonContent.Create(body);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private async Task<ExpiryRunDto> RunExpiryAsync()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/contract-notifications/runs");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ExpiryRunDto>(await response.Content.ReadFromJsonAsync<ExpiryRunDto>());
    }

    private static Dictionary<string, string> AttributesFor(string type) => type switch
    {
        "Hardware" => new() { ["manufacturer"] = "Dell", ["model"] = "Latitude 5450" },
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
        request.Headers.Add(ContractAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record VendorDto(Guid Id, string Name, bool IsActive, int ContractCount);

    private sealed record ContractDto(
        Guid Id,
        Guid VendorId,
        string VendorName,
        string ContractNumber,
        string Name,
        string Type,
        string StartDate,
        string EndDate,
        bool IsActive,
        string Status,
        int DaysRemaining,
        int CoveredCiCount);

    private sealed record ContractPageDto(List<ContractDto> Items, int Total, int Page, int PageSize);

    private sealed record CoverageDto(
        Guid? ContractId,
        string? ContractName,
        string? ContractNumber,
        string? VendorName,
        string? ContractEndDate,
        string? PurchaseDate,
        string? WarrantyExpiresAt,
        string? WarrantyStatus,
        int? WarrantyDaysRemaining);

    private sealed record CiDto(Guid Id, string Type, string Name, string LifecycleState, CoverageDto Coverage);

    private sealed record CiPageDto(List<CiDto> Items, int Total, int Page, int PageSize);

    private sealed record NotificationDto(
        Guid Id,
        string Subject,
        Guid SubjectId,
        string SubjectName,
        string DueDate,
        int ThresholdDays,
        string Recipient,
        string Message,
        DateTimeOffset SentAt);

    private sealed record ExpiryRunDto(
        string RunDate,
        int ContractsScanned,
        int WarrantiesScanned,
        List<NotificationDto> Raised);

    private sealed class ContractApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public ContractApplication(
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
                        options.DefaultAuthenticateScheme = ContractAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = ContractAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = ContractAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ContractAuthenticationHandler>(
                        ContractAuthenticationHandler.TestScheme,
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

    private sealed class ContractAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "ContractTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "contract-test-user-id"),
                    new Claim(ClaimTypes.Name, "contract-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
