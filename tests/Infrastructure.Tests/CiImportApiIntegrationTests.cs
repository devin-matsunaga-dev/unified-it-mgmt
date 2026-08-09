using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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

[Collection(InfrastructureCollection.Name)]
public sealed class CiImportApiIntegrationTests : IAsyncLifetime
{
    private readonly ImportApplication _application;
    private HttpClient? _client;

    public CiImportApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new ImportApplication(
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
    /// The WP's first verification step: a hundred rows imported twice leave a hundred CIs, with the
    /// second run reporting every row as skipped.
    /// </summary>
    [Fact]
    public async Task Commit_SameFileTwice_CreatesOnceThenSkipsEveryRow()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var csv = LaptopCsv(marker, 100);

        var first = await CommitAsync(csv, HardwareMapping);

        Assert.Equal(100, first.TotalRows);
        Assert.Equal(100, first.Created);
        Assert.Equal(0, first.Updated);
        Assert.Equal(0, first.Failed);
        Assert.Equal(100, (await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200")).Total);

        var second = await CommitAsync(csv, HardwareMapping);

        Assert.Equal(100, second.Skipped);
        Assert.Equal(0, second.Created);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Failed);
        Assert.All(second.Rows, row => Assert.Equal("Skip", row.Action));
        Assert.Equal(100, (await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200")).Total);
    }

    /// <summary>
    /// The WP's second verification step: the malformed row is named by its line number and every other
    /// row still imports.
    /// </summary>
    [Fact]
    public async Task Commit_MalformedRow_IsReportedByLineNumberAndTheRestImport()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var csv = new StringBuilder("Name,Asset tag,Serial,Make,Model\n")
            .Append(CultureInfo.InvariantCulture, $"{marker}-good-1,{marker}-AT-1,{marker}-SN-1,Dell,Latitude 5550\n")
            // No manufacturer: a required attribute of a Hardware CI.
            .Append(CultureInfo.InvariantCulture, $"{marker}-bad,{marker}-AT-2,{marker}-SN-2,,Latitude 5550\n")
            .Append(CultureInfo.InvariantCulture, $"{marker}-good-2,{marker}-AT-3,{marker}-SN-3,Dell,Latitude 5550\n")
            .ToString();

        var report = await CommitAsync(csv, HardwareMapping);

        Assert.Equal(2, report.Created);
        Assert.Equal(1, report.Failed);
        var failed = Assert.Single(report.Rows, row => row.Action == "Error");
        Assert.Equal(3, failed.LineNumber);
        Assert.Contains("Manufacturer is required", Assert.Single(failed.Errors), StringComparison.Ordinal);
        Assert.Equal(2, (await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200")).Total);
    }

    [Fact]
    public async Task Preview_WritesNothing()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];

        var report = await PreviewAsync(LaptopCsv(marker, 3), HardwareMapping);

        Assert.True(report.IsDryRun);
        Assert.Equal(3, report.Created);
        Assert.Equal(0, (await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200")).Total);
    }

    [Fact]
    public async Task Commit_ChangedRow_UpdatesTheMatchedCiAndLeavesUnmappedFieldsAlone()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        await CommitAsync(LaptopCsv(marker, 1), HardwareMapping);
        var before = Assert.Single((await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200")).Items);
        // A field the import never maps, so the update must not touch it.
        using var describe = Authenticated(HttpMethod.Put, $"/api/cis/{before.Id}");
        describe.Content = JsonContent.Create(new
        {
            name = before.Name,
            assetTag = before.AssetTag,
            serialNumber = before.SerialNumber,
            description = "Set by hand",
            isActive = true,
            attributes = new Dictionary<string, string> { ["manufacturer"] = "Dell", ["model"] = "Latitude 5550" },
        });
        using var described = await _client!.SendAsync(describe);
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);

        var changed = $"Name,Asset tag,Serial,Make,Model\n{marker}-laptop-1,{marker}-AT-1,{marker}-SN-1,Lenovo,ThinkPad T14\n";
        var report = await CommitAsync(changed, HardwareMapping);

        Assert.Equal(1, report.Updated);
        Assert.Equal(before.Id, Assert.Single(report.Rows).MatchedCiId);
        var after = await GetAsync<CiDto>($"/api/cis/{before.Id}");
        Assert.Equal("Lenovo", after.Attributes["manufacturer"]);
        Assert.Equal("ThinkPad T14", after.Attributes["model"]);
        Assert.Equal("Set by hand", after.Description);
    }

    [Fact]
    public async Task Commit_BlankCellOnAnUpdate_DoesNotClearTheStoredValue()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        await CommitAsync(LaptopCsv(marker, 1), HardwareMapping);

        var blanked = $"Name,Asset tag,Serial,Make,Model\n{marker}-laptop-1,{marker}-AT-1,{marker}-SN-1,,\n";
        var report = await CommitAsync(blanked, HardwareMapping);

        Assert.Equal(1, report.Skipped);
        var ci = Assert.Single((await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200")).Items);
        Assert.Equal("Dell", (await GetAsync<CiDto>($"/api/cis/{ci.Id}")).Attributes["manufacturer"]);
    }

    [Fact]
    public async Task Commit_RowsSharingAnAssetTag_ImportsTheFirstAndReportsTheSecond()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var csv = new StringBuilder("Name,Asset tag,Serial,Make,Model\n")
            .Append(CultureInfo.InvariantCulture, $"{marker}-one,{marker}-AT-1,{marker}-SN-1,Dell,5550\n")
            .Append(CultureInfo.InvariantCulture, $"{marker}-two,{marker}-AT-1,{marker}-SN-2,Dell,5550\n")
            .ToString();

        var report = await CommitAsync(csv, HardwareMapping);

        Assert.Equal(1, report.Created);
        var failed = Assert.Single(report.Rows, row => row.Action == "Error");
        Assert.Equal(3, failed.LineNumber);
        Assert.Contains("already used by line 2", Assert.Single(failed.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_RowMatchingACiOfAnotherType_IsRefused()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        await CommitAsync(LaptopCsv(marker, 1), HardwareMapping);

        var asServer = $"Name,Asset tag,Serial,Hostname,OS,CPU cores,RAM\n{marker}-laptop-1,{marker}-AT-1,{marker}-SN-1,app-01,Ubuntu 24.04,8,32\n";
        var report = await CommitAsync(asServer, new
        {
            type = "Server",
            columns = new Dictionary<string, string>
            {
                ["name"] = "Name",
                ["assetTag"] = "Asset tag",
                ["serialNumber"] = "Serial",
                ["attributes.hostname"] = "Hostname",
                ["attributes.operatingSystem"] = "OS",
                ["attributes.cpuCores"] = "CPU cores",
                ["attributes.ramGb"] = "RAM",
            },
        });

        Assert.Equal(1, report.Failed);
        Assert.Contains(
            "already registered as a Hardware CI",
            Assert.Single(Assert.Single(report.Rows).Errors),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// WP-2.10's first verification step: one file holding a laptop, a server, a switch and a VM lands
    /// as four CIs of four types, each carrying only its own attributes.
    /// </summary>
    [Fact]
    public async Task Commit_MixedFileWithATypeColumn_LandsEachRowAsTheTypeItStates()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];

        var report = await CommitAsync(MixedCsv(marker, withTypeColumn: true), MixedMapping(withTypeColumn: true));

        Assert.Equal(4, report.Created);
        Assert.Equal(0, report.Failed);
        Assert.All(report.Rows, row => Assert.Equal("Column", row.TypeSource));
        var landed = await ByNameAsync(marker);
        Assert.Equal("Hardware", landed[$"{marker}-laptop"].Type);
        Assert.Equal("Dell", landed[$"{marker}-laptop"].Attributes["manufacturer"]);
        Assert.Equal("Server", landed[$"{marker}-server"].Type);
        Assert.Equal("Ubuntu 24.04", landed[$"{marker}-server"].Attributes["operatingSystem"]);
        // The server row's own columns only: the laptop's Make column is not part of a Server CI.
        Assert.DoesNotContain("manufacturer", landed[$"{marker}-server"].Attributes.Keys);
        Assert.Equal("NetworkDevice", landed[$"{marker}-switch"].Type);
        Assert.Equal("48", landed[$"{marker}-switch"].Attributes["portCount"]);
        Assert.Equal("Virtual", landed[$"{marker}-vm"].Type);
        Assert.Equal("VMware ESXi", landed[$"{marker}-vm"].Attributes["hypervisor"]);
    }

    /// <summary>
    /// WP-2.10's second verification step: a blank and an unrecognised type cell are refused by line
    /// number while every other row imports.
    /// </summary>
    [Fact]
    public async Task Commit_MixedFileWithABlankOrUnknownTypeCell_RefusesThoseRowsByLineNumber()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var csv = new StringBuilder(MixedHeader(withTypeColumn: true))
            .Append(CultureInfo.InvariantCulture, $"{marker}-laptop,{marker}-AT-1,{marker}-SN-1,Hardware,Dell,Latitude 5550,,,,,,,,,\n")
            .Append(CultureInfo.InvariantCulture, $"{marker}-blank,{marker}-AT-2,{marker}-SN-2,,Dell,Latitude 5550,,,,,,,,,\n")
            .Append(CultureInfo.InvariantCulture, $"{marker}-unknown,{marker}-AT-3,{marker}-SN-3,Photocopier,Dell,Latitude 5550,,,,,,,,,\n")
            .Append(CultureInfo.InvariantCulture, $"{marker}-switch,{marker}-AT-4,{marker}-SN-4,Network device,,,,,,,10.20.0.1,Cisco,48,,\n")
            .ToString();

        var report = await CommitAsync(csv, MixedMapping(withTypeColumn: true));

        Assert.Equal(2, report.Created);
        Assert.Equal(2, report.Failed);
        var blank = Assert.Single(report.Rows, row => row.LineNumber == 3);
        Assert.Contains("blank", Assert.Single(blank.Errors), StringComparison.Ordinal);
        var unknown = Assert.Single(report.Rows, row => row.LineNumber == 4);
        Assert.Contains("'Photocopier' is not a CI type", Assert.Single(unknown.Errors), StringComparison.Ordinal);
        Assert.Equal(2, (await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200")).Total);
    }

    /// <summary>
    /// WP-2.10's third verification step: with the type column dropped, the dry run shows a guess per
    /// row and committing produces the same four CIs.
    /// </summary>
    [Fact]
    public async Task MixedFileWithoutATypeColumn_GuessesEachRowsTypeAndCommitsToTheSameFourCis()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var csv = MixedCsv(marker, withTypeColumn: false);

        var dryRun = await PreviewAsync(csv, MixedMapping(withTypeColumn: false));

        Assert.Equal(4, dryRun.Created);
        Assert.All(dryRun.Rows, row => Assert.Equal("Inferred", row.TypeSource));
        Assert.Equal(
            ["Hardware", "Server", "NetworkDevice", "Virtual"],
            dryRun.Rows.OrderBy(row => row.LineNumber).Select(row => row.Type));

        var report = await CommitAsync(csv, MixedMapping(withTypeColumn: false, acceptInferredTypes: true));

        Assert.Equal(4, report.Created);
        var landed = await ByNameAsync(marker);
        Assert.Equal("Hardware", landed[$"{marker}-laptop"].Type);
        Assert.Equal("Server", landed[$"{marker}-server"].Type);
        Assert.Equal("NetworkDevice", landed[$"{marker}-switch"].Type);
        Assert.Equal("Virtual", landed[$"{marker}-vm"].Type);
    }

    /// <summary>
    /// A guessed type is permanent once written, so the commit is refused until the wizard confirms the
    /// operator has read the dry run. The dry run itself never needs the confirmation.
    /// </summary>
    [Fact]
    public async Task Commit_GuessedTypesWithoutTheOperatorsConfirmation_IsRefused()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        using var content = Multipart(MixedCsv(marker, withTypeColumn: false));
        content.Add(
            new StringContent(JsonSerializer.Serialize(MixedMapping(withTypeColumn: false))),
            "mapping");
        using var request = Authenticated(HttpMethod.Post, "/api/ci-imports/commit");
        request.Content = content;

        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("whose CI type was guessed", problem, StringComparison.Ordinal);
        Assert.Equal(0, (await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200")).Total);
    }

    /// <summary>
    /// A row that names a type nothing in the file distinguishes cannot be guessed, and is reported
    /// rather than assigned one.
    /// </summary>
    [Fact]
    public async Task Commit_RowWhoseTypeCannotBeGuessed_IsReportedAndTheRestImport()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var csv = new StringBuilder(MixedHeader(withTypeColumn: false))
            // Hostname and RAM belong to both Server and Virtual, so this row says neither.
            .Append(CultureInfo.InvariantCulture, $"{marker}-ambiguous,{marker}-AT-1,{marker}-SN-1,,,box-01,,,32,,,,,\n")
            .Append(CultureInfo.InvariantCulture, $"{marker}-laptop,{marker}-AT-2,{marker}-SN-2,Dell,Latitude 5550,,,,,,,,,\n")
            .ToString();

        var report = await CommitAsync(csv, MixedMapping(withTypeColumn: false, acceptInferredTypes: true));

        Assert.Equal(1, report.Created);
        var failed = Assert.Single(report.Rows, row => row.Action == "Error");
        Assert.Equal(2, failed.LineNumber);
        Assert.Null(failed.Type);
        Assert.Contains("could not be guessed", Assert.Single(failed.Errors), StringComparison.Ordinal);
    }

    /// <summary>WP-2.5's guard, restated for a mixed file: a CI's type is permanent.</summary>
    [Fact]
    public async Task Commit_MixedRowMatchingACiOfAnotherType_IsStillRefused()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        await CommitAsync(LaptopCsv(marker, 1), HardwareMapping);
        var asServer = new StringBuilder(MixedHeader(withTypeColumn: true))
            .Append(CultureInfo.InvariantCulture, $"{marker}-laptop-1,{marker}-AT-1,{marker}-SN-1,Server,,,app-01,Ubuntu 24.04,8,32,,,,,\n")
            .ToString();

        var report = await CommitAsync(asServer, MixedMapping(withTypeColumn: true));

        Assert.Equal(1, report.Failed);
        Assert.Contains(
            "already registered as a Hardware CI",
            Assert.Single(Assert.Single(report.Rows).Errors),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Columns_ForAMixedImport_OffersTheTypeColumnAndEveryTypesAttributes()
    {
        using var content = Multipart(MixedCsv("mixedcols", withTypeColumn: true));
        content.Add(new StringContent("Mixed"), "type");
        using var request = Authenticated(HttpMethod.Post, "/api/ci-imports/columns");
        request.Content = content;

        using var response = await _client!.SendAsync(request);
        var body = Assert.IsType<ColumnsDto>(await response.Content.ReadFromJsonAsync<ColumnsDto>());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(body.Targets, target => target.Key == "type");
        Assert.Contains(body.Targets, target => target.Key == "attributes.manufacturer");
        Assert.Contains(body.Targets, target => target.Key == "attributes.hypervisor");
        // Required-ness travels per type: no column of a mixed file is required of every row.
        var hostname = Assert.Single(body.Targets, target => target.Key == "attributes.hostname");
        Assert.False(hostname.IsRequired);
        Assert.Equal(["Server", "Virtual"], hostname.Types!.Select(entry => entry.Type).Order());
        Assert.Equal("Type", body.SuggestedMapping["type"]);
    }

    [Fact]
    public async Task Columns_WithAnUnknownTypeSelection_ReturnsValidationProblem()
    {
        using var content = Multipart(LaptopCsv("badtype", 1));
        content.Add(new StringContent("Photocopier"), "type");
        using var request = Authenticated(HttpMethod.Post, "/api/ci-imports/columns");
        request.Content = content;

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'Mixed'", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Columns_ProposesAMappingForTheHeadersItRecognises()
    {
        using var content = Multipart(LaptopCsv("suggest", 1));
        content.Add(new StringContent("Hardware"), "type");
        using var request = Authenticated(HttpMethod.Post, "/api/ci-imports/columns");
        request.Content = content;

        using var response = await _client!.SendAsync(request);
        var columns = await response.Content.ReadFromJsonAsync<ColumnsDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Assert.IsType<ColumnsDto>(columns);
        Assert.Equal(["Name", "Asset tag", "Serial", "Make", "Model"], body.Headers);
        Assert.Equal(1, body.RowCount);
        Assert.Equal("Name", body.SuggestedMapping["name"]);
        Assert.Equal("Asset tag", body.SuggestedMapping["assetTag"]);
        Assert.Equal("Serial", body.SuggestedMapping["serialNumber"]);
        Assert.Equal("Model", body.SuggestedMapping["attributes.model"]);
        Assert.Contains(body.Targets, target => target.Key == "attributes.manufacturer" && target.IsRequired);
    }

    [Fact]
    public async Task Preview_MappingWithoutADedupeKey_ReturnsValidationProblem()
    {
        using var content = Multipart(LaptopCsv("nokey", 1));
        content.Add(
            new StringContent(JsonSerializer.Serialize(new
            {
                type = "Hardware",
                columns = new Dictionary<string, string>
                {
                    ["name"] = "Name",
                    ["attributes.manufacturer"] = "Make",
                    ["attributes.model"] = "Model",
                },
            })),
            "mapping");
        using var request = Authenticated(HttpMethod.Post, "/api/ci-imports/preview");
        request.Content = content;

        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("matched to existing CIs", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_FileThatIsNotACsvOrWorkbook_ReturnsProblemDetails()
    {
        using var content = Multipart("Name\nlaptop\n", "assets.txt");
        content.Add(new StringContent(JsonSerializer.Serialize(HardwareMapping)), "mapping");
        using var request = Authenticated(HttpMethod.Post, "/api/ci-imports/preview");
        request.Content = content;

        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Upload a .csv or .xlsx file.", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_WritesAnAuditEntryForTheImport()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        await CommitAsync(LaptopCsv(marker, 2), HardwareMapping);

        await using var scope = _application.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().AuditEntries
            .Where(entry => entry.EntityType == "CiImport" && entry.Action == "Imported")
            .ToListAsync();

        Assert.NotEmpty(audits);
    }

    [Fact]
    public async Task Commit_AsEndUser_IsForbidden()
    {
        using var content = Multipart(LaptopCsv("forbidden", 1));
        content.Add(new StringContent(JsonSerializer.Serialize(HardwareMapping)), "mapping");
        using var request = Authenticated(HttpMethod.Post, "/api/ci-imports/commit", "EndUser");
        request.Content = content;

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static object HardwareMapping => new
    {
        type = "Hardware",
        columns = new Dictionary<string, string>
        {
            ["name"] = "Name",
            ["assetTag"] = "Asset tag",
            ["serialNumber"] = "Serial",
            ["attributes.manufacturer"] = "Make",
            ["attributes.model"] = "Model",
        },
    };

    /// <summary>
    /// One sheet of everything, the file WP-2.10 exists for: a laptop, a server, a switch and a VM, each
    /// filling only its own type's columns and leaving the rest blank.
    /// </summary>
    private static string MixedCsv(string marker, bool withTypeColumn)
    {
        var csv = new StringBuilder(MixedHeader(withTypeColumn));
        var rows = new[]
        {
            ($"{marker}-laptop", "Hardware", "Dell,Latitude 5550,,,,,,,,,"),
            ($"{marker}-server", "Server", ",,app-01,Ubuntu 24.04,8,32,,,,,"),
            ($"{marker}-switch", "Network device", ",,,,,,10.20.0.1,Cisco,48,,"),
            ($"{marker}-vm", "Virtual", ",,vm-01,,,16,,,,VMware ESXi,4"),
        };
        var line = 1;
        foreach (var (name, type, attributes) in rows)
        {
            var typeCell = withTypeColumn ? $"{type}," : string.Empty;
            csv.Append(CultureInfo.InvariantCulture, $"{name},{marker}-AT-{line},{marker}-SN-{line},{typeCell}{attributes}\n");
            line++;
        }

        return csv.ToString();
    }

    private static string MixedHeader(bool withTypeColumn) =>
        "Name,Asset tag,Serial,"
        + (withTypeColumn ? "Type," : string.Empty)
        + "Make,Model,Hostname,OS,CPU cores,RAM,Management IP,Vendor,Ports,Hypervisor,vCPU cores\n";

    private static object MixedMapping(bool withTypeColumn, bool acceptInferredTypes = false)
    {
        var columns = new Dictionary<string, string>
        {
            ["name"] = "Name",
            ["assetTag"] = "Asset tag",
            ["serialNumber"] = "Serial",
            ["attributes.manufacturer"] = "Make",
            ["attributes.model"] = "Model",
            ["attributes.hostname"] = "Hostname",
            ["attributes.operatingSystem"] = "OS",
            ["attributes.cpuCores"] = "CPU cores",
            ["attributes.ramGb"] = "RAM",
            ["attributes.managementIp"] = "Management IP",
            ["attributes.vendor"] = "Vendor",
            ["attributes.portCount"] = "Ports",
            ["attributes.hypervisor"] = "Hypervisor",
            ["attributes.vcpuCores"] = "vCPU cores",
        };
        if (withTypeColumn)
        {
            columns["type"] = "Type";
        }

        return new { type = "Mixed", columns, acceptInferredTypes };
    }

    private async Task<Dictionary<string, CiDto>> ByNameAsync(string marker)
    {
        var page = await GetAsync<CiPageDto>($"/api/cis?search={marker}&pageSize=200");
        var byName = new Dictionary<string, CiDto>(StringComparer.Ordinal);
        foreach (var item in page.Items)
        {
            byName[item.Name] = await GetAsync<CiDto>($"/api/cis/{item.Id}");
        }

        return byName;
    }

    private static string LaptopCsv(string marker, int rows)
    {
        var csv = new StringBuilder("Name,Asset tag,Serial,Make,Model\n");
        for (var index = 1; index <= rows; index++)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{marker}-laptop-{index},{marker}-AT-{index},{marker}-SN-{index},Dell,Latitude 5550\n");
        }

        return csv.ToString();
    }

    private Task<ReportDto> PreviewAsync(string csv, object mapping) => SendAsync("preview", csv, mapping);

    private Task<ReportDto> CommitAsync(string csv, object mapping) => SendAsync("commit", csv, mapping);

    private async Task<ReportDto> SendAsync(string step, string csv, object mapping)
    {
        using var content = Multipart(csv);
        content.Add(new StringContent(JsonSerializer.Serialize(mapping)), "mapping");
        using var request = Authenticated(HttpMethod.Post, $"/api/ci-imports/{step}");
        request.Content = content;
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ReportDto>(await response.Content.ReadFromJsonAsync<ReportDto>());
    }

    private static MultipartFormDataContent Multipart(string csv, string fileName = "assets.csv")
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", fileName);
        return content;
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
        request.Headers.Add(ImportAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record ReportDto(
        bool IsDryRun,
        int TotalRows,
        int Created,
        int Updated,
        int Skipped,
        int Failed,
        List<ReportRowDto> Rows);

    private sealed record ReportRowDto(
        int LineNumber,
        string Action,
        string? Name,
        string? AssetTag,
        string? SerialNumber,
        Guid? MatchedCiId,
        List<string> Errors,
        string? Type,
        string? TypeSource);

    private sealed record ColumnsDto(
        string FileName,
        List<string> Headers,
        List<List<string>> SampleRows,
        int RowCount,
        List<TargetDto> Targets,
        Dictionary<string, string> SuggestedMapping);

    private sealed record TargetDto(
        string Key,
        string Label,
        bool IsRequired,
        string Kind,
        List<TargetTypeDto>? Types);

    private sealed record TargetTypeDto(string Type, bool IsRequired);

    private sealed record CiDto(
        Guid Id,
        string Type,
        string Name,
        string? AssetTag,
        string? SerialNumber,
        string? Description,
        string LifecycleState,
        Dictionary<string, string> Attributes);

    private sealed record CiPageDto(List<CiDto> Items, int Total, int Page, int PageSize);

    private sealed class ImportApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public ImportApplication(
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
                        options.DefaultAuthenticateScheme = ImportAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = ImportAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = ImportAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ImportAuthenticationHandler>(
                        ImportAuthenticationHandler.TestScheme,
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

    private sealed class ImportAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "ImportTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "import-test-user-id"),
                    new Claim(ClaimTypes.Name, "import-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
