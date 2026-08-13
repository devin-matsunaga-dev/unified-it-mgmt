using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
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
/// WP-4.4 end to end: an inventory file for five machines through the API, the catalogue normalising
/// the raw names it carries, and the compliance report turning a pool of three against five installs
/// into an over-deployment and a notification.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SoftwareInventoryApiIntegrationTests : IAsyncLifetime
{
    private readonly SoftwareApplication _application;
    private HttpClient? _client;

    /// <summary>Keeps this class's CIs, products and pools apart from everything else in the shared database.</summary>
    private readonly string _marker = Guid.NewGuid().ToString("N")[..8];

    public SoftwareInventoryApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new SoftwareApplication(
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

        // Deleting a CI reaches Helpdesk through ITicketLinkDirectory and Monitoring through
        // IMonitoredAddressDirectory, and an unmigrated schema behind either answers 42P01 rather than
        // a 404. The sixth package to meet that trap; migrating all four is the cure.
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The WP's first verification step: inventory for five machines lands, and its raw names are
    /// listed as normalised products rather than as the strings the agent reported.
    /// </summary>
    [Fact]
    public async Task Import_FiveMachines_NormalisesTheRawNamesIntoCatalogueProducts()
    {
        // Every pattern carries the marker. The fixture database is shared with the seeder tests and
        // with every other class here, and a rule's (kind, pattern) is unique estate-wide — an
        // unmarked "google chrome" here is a rule the seeded catalogue also wants to write.
        var chrome = await CreateProductAsync("Chrome", "Google");
        await CreateRuleAsync(chrome.Id, "Prefix", $"google chrome {_marker}");
        var office = await CreateProductAsync("Office Professional Plus", "Microsoft");
        await CreateRuleAsync(office.Id, "Prefix", $"microsoft office professional plus {_marker}");
        var tags = await CreateLaptopsAsync(5);

        var report = await CommitAsync(InventoryCsv(tags));

        Assert.Equal(10, report.TotalRows);
        Assert.Equal(10, report.Created);
        Assert.Equal(0, report.Failed);
        Assert.Equal(5, report.MachinesMatched);
        Assert.Equal(10, report.Normalised);

        var compliance = await GetAsync<ComplianceDto>($"/api/software-compliance?search={_marker}");
        var chromeRow = compliance.Rows.Single(row => row.ProductId == chrome.Id);
        Assert.Equal(5, chromeRow.InstalledCiCount);
        Assert.Equal(5, compliance.Rows.Single(row => row.ProductId == office.Id).InstalledCiCount);
    }

    /// <summary>
    /// The WP's second verification step, and the one the whole package exists for: a pool of three
    /// against five installs is flagged and notified.
    /// </summary>
    [Fact]
    public async Task Compliance_APoolOfThreeAgainstFiveInstalls_IsOverDeployedAndNotified()
    {
        var acrobat = await CreateProductAsync("Acrobat Pro", "Adobe");
        await CreateRuleAsync(acrobat.Id, "Prefix", $"adobe acrobat pro {_marker}");
        var tags = await CreateLaptopsAsync(5);
        await CommitAsync(AcrobatCsv(tags));
        await CreatePoolAsync(acrobat.Id, entitlements: 3);

        var compliance = await GetAsync<ComplianceDto>($"/api/software-compliance?search={_marker}");
        var row = compliance.Rows.Single(item => item.ProductId == acrobat.Id);

        Assert.Equal(5, row.InstalledCiCount);
        Assert.Equal(3, row.Entitled);
        Assert.Equal(2, row.Overage);
        Assert.Equal("OverDeployed", row.State);

        var run = await PostAsync<ComplianceRunDto>("/api/software-compliance/runs");
        var raised = Assert.Single(run.Raised, notice => notice.SubjectId == acrobat.Id);
        Assert.Equal(2, raised.ThresholdDays);
        Assert.Contains("installed on 5 devices", raised.Message);
        Assert.Contains("only 3 are entitled", raised.Message);

        // Idempotent within a day: the same shortfall is not notified twice.
        var second = await PostAsync<ComplianceRunDto>("/api/software-compliance/runs");
        Assert.DoesNotContain(second.Raised, notice => notice.SubjectId == acrobat.Id);
    }

    /// <summary>
    /// A product nobody has ever bought a licence for is Unlicensed rather than over-deployed. Only the
    /// second one notifies — otherwise every free utility in the estate would mail somebody nightly.
    /// </summary>
    [Fact]
    public async Task Compliance_AProductWithNoPoolAtAll_IsUnlicensedAndNotifiesNobody()
    {
        var product = await CreateProductAsync("Free Utility", "Contoso");
        await CreateRuleAsync(product.Id, "Prefix", $"contoso free utility {_marker}");
        var tags = await CreateLaptopsAsync(2);
        await CommitAsync(Csv(
            ["asset tag", "software", "version"],
            [.. tags.Select(tag => new[] { tag, $"Contoso Free Utility {_marker}", "1.0" })]));

        var compliance = await GetAsync<ComplianceDto>($"/api/software-compliance?search={_marker}");
        var row = compliance.Rows.Single(item => item.ProductId == product.Id);
        Assert.Equal("Unlicensed", row.State);
        Assert.Equal(0, row.Entitled);

        var run = await PostAsync<ComplianceRunDto>("/api/software-compliance/runs");
        Assert.DoesNotContain(run.Raised, notice => notice.SubjectId == product.Id);
    }

    /// <summary>An expired pool entitles nothing, which is what makes the renewal notice matter.</summary>
    [Fact]
    public async Task Compliance_AnExpiredPool_StopsEntitlingAndTurnsTheProductOverDeployed()
    {
        var product = await CreateProductAsync("Lapsed Suite", "Contoso");
        await CreateRuleAsync(product.Id, "Prefix", $"contoso lapsed suite {_marker}");
        var tags = await CreateLaptopsAsync(2);
        await CommitAsync(Csv(
            ["asset tag", "software"],
            [.. tags.Select(tag => new[] { tag, $"Contoso Lapsed Suite {_marker}" })]));
        await CreatePoolAsync(product.Id, entitlements: 10, expiresInDays: -1);

        var compliance = await GetAsync<ComplianceDto>($"/api/software-compliance?search={_marker}");
        var row = compliance.Rows.Single(item => item.ProductId == product.Id);

        Assert.Equal(0, row.Entitled);
        Assert.Equal(1, row.ExpiredPoolCount);
        Assert.Equal("OverDeployed", row.State);
    }

    /// <summary>A licence pool rides the WP-2.6 renewal pass on the same 30/7/0 thresholds.</summary>
    [Fact]
    public async Task ExpiryPass_ALicencePoolInsideTheWindow_RaisesOneRenewalNoticeAndThenIsSilent()
    {
        var product = await CreateProductAsync("Renewable Suite", "Contoso");
        var pool = await CreatePoolAsync(product.Id, entitlements: 20, expiresInDays: 5);

        var run = await PostAsync<ExpiryRunDto>("/api/contract-notifications/runs");
        var raised = Assert.Single(run.Raised, notice => notice.SubjectId == pool.Id);

        Assert.Equal(7, raised.ThresholdDays);
        Assert.Equal("License", raised.Subject);
        Assert.Contains("20-seat licence", raised.SubjectName);
        Assert.True(run.LicensePoolsScanned > 0);

        var second = await PostAsync<ExpiryRunDto>("/api/contract-notifications/runs");
        Assert.DoesNotContain(second.Raised, notice => notice.SubjectId == pool.Id);
    }

    /// <summary>
    /// A rule added after the fact reaches the inventory already imported — otherwise the catalogue
    /// only ever applies to the future and the unrecognised list can never be worked down.
    /// </summary>
    [Fact]
    public async Task Normalise_ARuleAddedAfterTheImport_ReachesTheInstallsAlreadyRecorded()
    {
        var tags = await CreateLaptopsAsync(2);
        var rawName = $"Contoso VPN Client {_marker}";
        var report = await CommitAsync(Csv(
            ["asset tag", "software"],
            [.. tags.Select(tag => new[] { tag, rawName })]));

        Assert.Equal(0, report.Normalised);
        Assert.Equal(2, report.Failed + report.Created - report.Failed);
        Assert.Contains(rawName, report.UnrecognisedNames);

        var unrecognised = await GetAsync<List<UnrecognisedDto>>("/api/installed-software/unrecognised?limit=200");
        var pending = Assert.Single(unrecognised, row => row.RawName == rawName);
        Assert.Equal(2, pending.CiCount);

        var product = await CreateProductAsync("VPN Client", "Contoso");
        await CreateRuleAsync(product.Id, "Prefix", $"contoso vpn client {_marker}");
        var run = await PostAsync<NormalisationRunDto>("/api/installed-software/normalisations");

        Assert.True(run.Normalised >= 2);
        var installs = await GetAsync<InstalledPageDto>($"/api/installed-software?productId={product.Id}&pageSize=200");
        Assert.Equal(2, installs.Total);
        Assert.All(installs.Items, install => Assert.Equal(product.Id, install.ProductId));
    }

    /// <summary>A second import of the same file refreshes what is there rather than doubling it.</summary>
    [Fact]
    public async Task Commit_TheSameFileTwice_RecordsEachInstallOnceAndRefreshesIt()
    {
        var product = await CreateProductAsync("Repeatable", "Contoso");
        await CreateRuleAsync(product.Id, "Prefix", $"contoso repeatable {_marker}");
        var tags = await CreateLaptopsAsync(2);
        var csv = Csv(["asset tag", "software", "version"],
            [.. tags.Select(tag => new[] { tag, $"Contoso Repeatable {_marker}", "1.0" })]);

        var first = await CommitAsync(csv);
        var second = await CommitAsync(csv);

        Assert.Equal(2, first.Created);
        Assert.Equal(0, second.Created);
        Assert.Equal(2, second.Updated);

        var installs = await GetAsync<InstalledPageDto>($"/api/installed-software?productId={product.Id}&pageSize=200");
        Assert.Equal(2, installs.Total);
        Assert.All(installs.Items, install => Assert.Equal(2, install.SightingCount));
    }

    /// <summary>The preview writes nothing. It is the same code path, so it is also literally the plan.</summary>
    [Fact]
    public async Task Preview_ReportsWhatTheCommitWouldDoAndWritesNothing()
    {
        var tags = await CreateLaptopsAsync(2);
        var csv = Csv(["asset tag", "software"], [.. tags.Select(tag => new[] { tag, $"Preview Only {_marker}" })]);

        var preview = await PostFileAsync<ImportReportDto>("/api/software-imports/preview", csv);

        Assert.True(preview.IsDryRun);
        Assert.Equal(2, preview.Created);
        var installs = await GetAsync<InstalledPageDto>($"/api/installed-software?search={_marker}&pageSize=200");
        Assert.Equal(0, installs.Total);
    }

    /// <summary>
    /// The failure path a real export takes: some rows name machines the CMDB holds and some do not,
    /// and the file still imports the ones it can.
    /// </summary>
    [Fact]
    public async Task Commit_ARowNamingAMachineTheCmdbDoesNotHold_FailsThatRowAndImportsTheRest()
    {
        var tags = await CreateLaptopsAsync(1);
        var report = await CommitAsync(Csv(
            ["asset tag", "software"],
            [
                [tags[0], $"Contoso Thing {_marker}"],
                [$"LT-MISSING-{_marker}", $"Contoso Thing {_marker}"],
            ]));

        Assert.Equal(1, report.Created);
        Assert.Equal(1, report.Failed);
        var failed = Assert.Single(report.Rows, row => row.Action == "Error");
        Assert.Equal(3, failed.LineNumber);
        Assert.Contains("No CI matches", Assert.Single(failed.Errors));
    }

    /// <summary>A file naming one install twice is a defect in whatever produced it, not a merge.</summary>
    [Fact]
    public async Task Commit_AFileListingOneInstallTwice_FailsTheLaterRowNamingTheEarlierLine()
    {
        var tags = await CreateLaptopsAsync(1);
        var report = await CommitAsync(Csv(
            ["asset tag", "software", "version"],
            [
                [tags[0], $"Contoso Twice {_marker}", "1.0"],
                [tags[0], $"contoso  twice {_marker}", "1.0"],
            ]));

        Assert.Equal(1, report.Created);
        Assert.Equal(1, report.Failed);
        Assert.Contains("Line 2 already lists this software", Assert.Single(
            Assert.Single(report.Rows, row => row.Action == "Error").Errors));
    }

    /// <summary>The failure path for the file itself: refused whole, with the sentence naming its columns.</summary>
    [Fact]
    public async Task Commit_AFileWithNoSoftwareColumn_Is400NamingTheColumnsTheImportReads()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/software-imports/commit");
        request.Content = Multipart(Csv(["asset tag", "notes"], [["LT-0001", "nothing useful"]]));

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Contains("no column naming the software", problem!.Detail);
        Assert.Contains("'software'", problem.Detail);
    }

    /// <summary>One pattern cannot mean two products: that is a contradiction rather than a priority.</summary>
    [Fact]
    public async Task CreateRule_WithAPatternAlreadyInTheCatalogue_Is409()
    {
        var first = await CreateProductAsync("First", "Contoso");
        var second = await CreateProductAsync("Second", "Contoso");
        var pattern = $"contoso shared pattern {_marker}";
        await CreateRuleAsync(first.Id, "Prefix", pattern);

        using var request = Authenticated(HttpMethod.Post, "/api/software-normalisation-rules");
        request.Content = JsonContent.Create(new
        {
            productId = second.Id,
            matchKind = "Prefix",
            pattern,
            priority = 0,
        });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// Deleting a product with history behind it would silently un-normalise every install pointing at
    /// it, so it is refused and deactivation is offered instead (WP-1.9's rule for ticket categories).
    /// </summary>
    [Fact]
    public async Task DeleteProduct_WithInstallsBehindIt_Is409OfferingDeactivationInstead()
    {
        var product = await CreateProductAsync("Undeletable", "Contoso");
        await CreateRuleAsync(product.Id, "Prefix", $"contoso undeletable {_marker}");
        var tags = await CreateLaptopsAsync(1);
        await CommitAsync(Csv(["asset tag", "software"], [[tags[0], $"Contoso Undeletable {_marker}"]]));

        using var request = Authenticated(HttpMethod.Delete, $"/api/software-products/{product.Id}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Contains("Deactivate it instead", problem!.Detail);
    }

    [Fact]
    public async Task CreateProduct_TwiceForOnePublisherAndName_Is409()
    {
        var product = await CreateProductAsync("Duplicated", "Contoso");

        using var request = Authenticated(HttpMethod.Post, "/api/software-products");
        request.Content = JsonContent.Create(new { name = product.Name.ToUpperInvariant(), publisher = "contoso", category = (string?)null, notes = (string?)null });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>Every write in this module is audited; software inventory is no exception.</summary>
    [Fact]
    public async Task SoftwareWrites_AreAudited()
    {
        var product = await CreateProductAsync("Audited", "Contoso");
        await CreateRuleAsync(product.Id, "Prefix", $"contoso audited {_marker}");
        var tags = await CreateLaptopsAsync(1);
        await CommitAsync(Csv(["asset tag", "software"], [[tags[0], $"Contoso Audited {_marker}"]]));
        await CreatePoolAsync(product.Id, entitlements: 1);

        await using var scope = _application.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entityId = product.Id.ToString();
        var created = await platform.AuditEntries.SingleAsync(audit =>
            audit.EntityType == "SoftwareProduct" && audit.EntityId == entityId && audit.Action == "Created");
        Assert.Equal("software-test-user-id", created.ActorId);

        var audits = await platform.AuditEntries.Where(audit =>
            audit.EntityType == "LicensePool" || audit.EntityType == "InstalledSoftware"
            || audit.EntityType == "SoftwareNormalisationRule").ToListAsync();
        Assert.Contains(audits, audit => audit.EntityType == "SoftwareNormalisationRule" && audit.Action == "Created");
        Assert.Contains(audits, audit => audit.EntityType == "LicensePool" && audit.Action == "Created");
        Assert.Contains(audits, audit => audit.EntityType == "InstalledSoftware" && audit.Action == "Imported");
    }

    /// <summary>An install is a property of one machine, so retiring the machine takes it with it.</summary>
    [Fact]
    public async Task DeletingACi_TakesItsInstalledSoftwareWithIt()
    {
        var product = await CreateProductAsync("Cascading", "Contoso");
        await CreateRuleAsync(product.Id, "Prefix", $"contoso cascading {_marker}");
        var tags = await CreateLaptopsAsync(1);
        await CommitAsync(Csv(["asset tag", "software"], [[tags[0], $"Contoso Cascading {_marker}"]]));
        var installs = await GetAsync<InstalledPageDto>($"/api/installed-software?productId={product.Id}&pageSize=200");
        var ciId = Assert.Single(installs.Items).CiId;

        using var request = Authenticated(HttpMethod.Delete, $"/api/cis/{ciId}", "Admin");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await GetAsync<InstalledPageDto>($"/api/installed-software?productId={product.Id}&pageSize=200");
        Assert.Equal(0, after.Total);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<string[]> CreateLaptopsAsync(int count)
    {
        var tags = new string[count];
        for (var index = 0; index < count; index++)
        {
            var tag = $"LT-{_marker}-{index:00}";
            using var request = Authenticated(HttpMethod.Post, "/api/cis");
            request.Content = JsonContent.Create(new
            {
                type = "Hardware",
                name = $"Laptop {tag}",
                assetTag = tag,
                serialNumber = $"SN-{_marker}-{index:00}",
                description = (string?)null,
                attributes = new Dictionary<string, string?> { ["manufacturer"] = "Dell", ["model"] = "Latitude 7450" },
            });
            using var response = await _client!.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            tags[index] = tag;
        }

        return tags;
    }

    private async Task<ProductDto> CreateProductAsync(string name, string publisher)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/software-products");
        request.Content = JsonContent.Create(new
        {
            name = $"{name} {_marker}",
            publisher,
            category = (string?)null,
            notes = (string?)null,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private async Task CreateRuleAsync(Guid productId, string matchKind, string pattern)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/software-normalisation-rules");
        request.Content = JsonContent.Create(new { productId, matchKind, pattern, priority = 0 });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<PoolDto> CreatePoolAsync(Guid productId, int entitlements, int? expiresInDays = null)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/license-pools");
        request.Content = JsonContent.Create(new
        {
            productId,
            name = $"Pool {_marker} {Guid.NewGuid():N}"[..24],
            reference = (string?)null,
            entitlements,
            purchaseDate = (string?)null,
            expiresAt = expiresInDays is { } days
                ? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd")
                : null,
            notes = (string?)null,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PoolDto>())!;
    }

    private Task<ImportReportDto> CommitAsync(string csv) =>
        PostFileAsync<ImportReportDto>("/api/software-imports/commit", csv);

    private async Task<T> PostFileAsync<T>(string uri, string csv)
    {
        using var request = Authenticated(HttpMethod.Post, uri);
        request.Content = Multipart(csv);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static MultipartFormDataContent Multipart(string csv, string fileName = "inventory.csv")
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", fileName);
        return content;
    }

    private string InventoryCsv(IReadOnlyList<string> tags) => Csv(
        ["asset tag", "software", "publisher", "version"],
        [
            .. tags.SelectMany(tag => new[]
            {
                new[] { tag, $"Google Chrome {_marker} 121.0.6167.140", "Google LLC", "121.0.6167.140" },
                new[] { tag, $"Microsoft Office Professional Plus {_marker} 2021 - en-us", "Microsoft Corporation", "16.0.14332" },
            }),
        ]);

    private string AcrobatCsv(IReadOnlyList<string> tags) => Csv(
        ["asset tag", "software", "version"],
        [.. tags.Select(tag => new[] { tag, $"Adobe Acrobat Pro {_marker} (64-bit)", "24.001" })]);

    private static string Csv(string[] headers, IReadOnlyList<string[]> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', headers));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(cell => cell.Contains(',') ? $"\"{cell}\"" : cell)));
        }

        return builder.ToString();
    }

    private async Task<T> GetAsync<T>(string uri)
    {
        using var request = Authenticated(HttpMethod.Get, uri);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string uri)
    {
        using var request = Authenticated(HttpMethod.Post, uri);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(SoftwareAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record ProductDto(Guid Id, string Name, string Publisher, bool IsActive, int InstallCount);

    private sealed record PoolDto(Guid Id, Guid ProductId, string Name, int Entitlements, string? Status);

    private sealed record ComplianceRowDto(
        Guid ProductId,
        string ProductName,
        string Publisher,
        int InstalledCiCount,
        int InstallCount,
        int Entitled,
        int LicensePoolCount,
        int ExpiredPoolCount,
        int Overage,
        string State);

    private sealed record ComplianceDto(
        DateOnly GeneratedOn,
        int ProductCount,
        int OverDeployedCount,
        int UnlicensedCount,
        List<ComplianceRowDto> Rows);

    private sealed record NoticeDto(
        Guid Id,
        string Subject,
        Guid SubjectId,
        string SubjectName,
        DateOnly DueDate,
        int ThresholdDays,
        string Message);

    private sealed record ComplianceRunDto(DateOnly Today, int ProductsChecked, int OverDeployed, List<NoticeDto> Raised);

    private sealed record ExpiryRunDto(
        DateOnly RunDate,
        int ContractsScanned,
        int WarrantiesScanned,
        List<NoticeDto> Raised,
        int LicensePoolsScanned);

    private sealed record NormalisationRunDto(int InstallsExamined, int Normalised, int Renormalised, int Unrecognised);

    private sealed record InstalledDto(
        Guid Id,
        Guid CiId,
        string CiName,
        string RawName,
        string? Version,
        Guid? ProductId,
        string? ProductName,
        int SightingCount);

    private sealed record InstalledPageDto(List<InstalledDto> Items, int Total, int Page, int PageSize);

    private sealed record UnrecognisedDto(string RawName, string? RawPublisher, int InstallCount, int CiCount);

    private sealed record ImportRowDto(
        int LineNumber,
        string Action,
        string? Machine,
        string? SoftwareName,
        Guid? CiId,
        Guid? ProductId,
        List<string> Errors);

    private sealed record ImportReportDto(
        bool IsDryRun,
        string FileName,
        int TotalRows,
        int Created,
        int Updated,
        int Failed,
        int MachinesMatched,
        int Normalised,
        int Unrecognised,
        List<ImportRowDto> Rows,
        List<string> UnrecognisedNames);

    private sealed record ProblemDto(string? Title, string? Detail);

    private sealed class SoftwareApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public SoftwareApplication(
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
                        options.DefaultAuthenticateScheme = SoftwareAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = SoftwareAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = SoftwareAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, SoftwareAuthenticationHandler>(
                        SoftwareAuthenticationHandler.TestScheme,
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

    private sealed class SoftwareAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "SoftwareTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "software-test-user-id"),
                    new Claim(ClaimTypes.Name, "software-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
