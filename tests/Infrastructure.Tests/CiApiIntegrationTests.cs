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

[Collection(InfrastructureCollection.Name)]
public sealed class CiApiIntegrationTests : IAsyncLifetime
{
    private readonly CiApplication _application;
    private HttpClient? _client;

    public CiApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new CiApplication(
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

    /// <summary>One CI of every type, which is the WP's first verification step.</summary>
    public static TheoryData<CiType, Dictionary<string, string?>> EveryCiType => new()
    {
        { CiType.Hardware, new() { ["manufacturer"] = "Dell", ["model"] = "Latitude 5550" } },
        {
            CiType.Server,
            new()
            {
                ["hostname"] = "app-01", ["operatingSystem"] = "Ubuntu 24.04",
                ["cpuCores"] = "8", ["ramGb"] = "32",
            }
        },
        {
            CiType.NetworkDevice,
            new() { ["managementIp"] = "10.20.0.1", ["vendor"] = "Cisco", ["portCount"] = "48" }
        },
        { CiType.Software, new() { ["vendor"] = "Atlassian", ["version"] = "9.4.1" } },
        {
            CiType.Virtual,
            new()
            {
                ["hostname"] = "vm-payroll", ["hypervisor"] = "Proxmox VE 8",
                ["vcpuCores"] = "4", ["ramGb"] = "16",
            }
        },
        {
            CiType.Logical,
            new() { ["purpose"] = "Payroll processing", ["serviceTier"] = "Gold" }
        },
    };

    [Theory]
    [MemberData(nameof(EveryCiType))]
    public async Task CreateCi_ForEveryType_PersistsTypeSpecificAttributes(
        CiType type,
        Dictionary<string, string?> attributes)
    {
        var created = await CreateCiAsync(type, $"{type} fixture {Guid.NewGuid():N}", attributes);

        Assert.Equal(type.ToString(), created.Type);
        foreach (var (key, value) in attributes)
        {
            Assert.Equal(value, created.Attributes[key]);
        }

        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{created.Id}");
        using var response = await _client!.SendAsync(request);
        var fetched = await response.Content.ReadFromJsonAsync<CiDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Id, Assert.IsType<CiDto>(fetched).Id);
        Assert.Equal(attributes.Count, fetched.Attributes.Count);
    }

    [Fact]
    public async Task CreateCi_PersistsAuditAndOutboxEvent()
    {
        var created = await CreateCiAsync(
            CiType.Software,
            $"Confluence {Guid.NewGuid():N}",
            new() { ["vendor"] = "Atlassian", ["version"] = "9.4.1" });

        await using var scope = _application.Services.CreateAsyncScope();
        var platformContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audits = await platformContext.AuditEntries
            .Where(entry => entry.EntityType == "Ci" && entry.EntityId == created.Id.ToString())
            .ToListAsync();

        Assert.Contains(audits, entry => entry.Action == "Created");
        Assert.Contains(
            await platformContext.Set<OutboxMessage>().ToListAsync(),
            message => message.MessageType.Contains(nameof(Contracts.Events.CiCreated), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateCi_MissingTypeSpecificAttribute_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "Server",
            name = "Server with no CPU count",
            attributes = new Dictionary<string, string>
            {
                ["hostname"] = "app-02",
                ["operatingSystem"] = "Ubuntu 24.04",
                ["ramGb"] = "32",
            },
        });

        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("CPU cores is required for a Server CI.", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCi_AttributeOfAnotherType_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "Software",
            name = "Software with a port count",
            attributes = new Dictionary<string, string>
            {
                ["vendor"] = "Atlassian",
                ["version"] = "9.4.1",
                ["portCount"] = "48",
            },
        });

        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'portCount' is not an attribute of a Software CI.", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCi_DuplicateAssetTag_ReturnsConflict()
    {
        var assetTag = $"AT-{Guid.NewGuid():N}"[..12];
        await CreateCiAsync(
            CiType.Hardware, "First laptop", new() { ["manufacturer"] = "Dell", ["model"] = "5550" }, assetTag);

        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "Hardware",
            name = "Second laptop",
            assetTag,
            attributes = new Dictionary<string, string> { ["manufacturer"] = "Dell", ["model"] = "5550" },
        });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The WP's third verification step: a field added at runtime reaches the form schema.</summary>
    [Fact]
    public async Task AddCustomField_AppearsOnTypeSchemaAndBindsOnCreate()
    {
        var key = $"rack{Guid.NewGuid():N}"[..10];
        var field = await AddCustomFieldAsync(CiType.Server, key, "Rack unit", "Text", isRequired: false);

        using var schemaRequest = Authenticated(HttpMethod.Get, "/api/ci-type-schemas");
        using var schemaResponse = await _client!.SendAsync(schemaRequest);
        var schemas = await schemaResponse.Content.ReadFromJsonAsync<List<CiTypeSchemaDto>>();
        var serverSchema = Assert.Single(
            Assert.IsType<List<CiTypeSchemaDto>>(schemas), schema => schema.Type == "Server");

        Assert.Contains(serverSchema.Attributes, attribute => attribute.Key == "cpuCores" && attribute.IsRequired);
        Assert.Contains(serverSchema.CustomFields, custom => custom.Id == field.Id && custom.Key == key);

        var created = await CreateCiAsync(
            CiType.Server,
            $"Server with custom field {Guid.NewGuid():N}",
            new()
            {
                ["hostname"] = "app-03", ["operatingSystem"] = "Ubuntu 24.04",
                ["cpuCores"] = "16", ["ramGb"] = "64",
            },
            customFields: new() { [key] = "U12" });

        var value = Assert.Single(created.CustomFields);
        Assert.Equal(key, value.Key);
        Assert.Equal("U12", value.Value);
    }

    [Fact]
    public async Task CreateCi_MissingRequiredCustomField_ReturnsValidationProblem()
    {
        var key = $"owner{Guid.NewGuid():N}"[..10];
        var field = await AddCustomFieldAsync(CiType.Logical, key, "Business owner", "Text", isRequired: true);
        try
        {
            using var request = Authenticated(HttpMethod.Post, "/api/cis");
            request.Content = JsonContent.Create(new
            {
                type = "Logical",
                name = "Logical CI without its required field",
                attributes = new Dictionary<string, string> { ["purpose"] = "Reporting" },
            });

            using var response = await _client!.SendAsync(request);
            var problem = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Business owner is required.", problem, StringComparison.Ordinal);
        }
        finally
        {
            // A required field left behind would make every later Logical CI in this class fail.
            using var cleanup = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
            using var cleanupResponse = await _client!.SendAsync(cleanup);
            Assert.Equal(HttpStatusCode.NoContent, cleanupResponse.StatusCode);
        }
    }

    [Fact]
    public async Task AddCustomField_ClashingWithBuiltInAttribute_ReturnsConflict()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/ci-custom-fields", "Admin");
        request.Content = JsonContent.Create(new
        {
            ciType = "Server",
            key = "hostname",
            label = "Hostname again",
            type = "Text",
            isRequired = false,
        });

        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("built-in attribute", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListCis_FiltersByType()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        await CreateCiAsync(
            CiType.Hardware, $"filter-{marker}-laptop",
            new() { ["manufacturer"] = "Lenovo", ["model"] = "T14" });
        await CreateCiAsync(
            CiType.Software, $"filter-{marker}-app",
            new() { ["vendor"] = "Atlassian", ["version"] = "9.4.1" });

        using var request = Authenticated(HttpMethod.Get, $"/api/cis?type=Hardware&search=filter-{marker}");
        using var response = await _client!.SendAsync(request);
        var page = await response.Content.ReadFromJsonAsync<CiPageDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(Assert.IsType<CiPageDto>(page).Items);
        Assert.Equal("Hardware", item.Type);
        Assert.Equal($"filter-{marker}-laptop", item.Name);
    }

    [Fact]
    public async Task UpdateCi_ClearingOptionalCustomField_RemovesStoredValue()
    {
        var key = $"tier{Guid.NewGuid():N}"[..10];
        await AddCustomFieldAsync(CiType.Logical, key, "Tier note", "Text", isRequired: false);
        var created = await CreateCiAsync(
            CiType.Logical,
            $"Logical clearable {Guid.NewGuid():N}",
            new() { ["purpose"] = "Reporting" },
            customFields: new() { [key] = "keep me for now" });
        Assert.Single(created.CustomFields);

        using var request = Authenticated(HttpMethod.Put, $"/api/cis/{created.Id}");
        request.Content = JsonContent.Create(new
        {
            name = created.Name,
            isActive = true,
            attributes = new Dictionary<string, string> { ["purpose"] = "Reporting" },
        });
        using var response = await _client!.SendAsync(request);
        var updated = await response.Content.ReadFromJsonAsync<CiDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Assert.IsType<CiDto>(updated).CustomFields);
    }

    [Fact]
    public async Task DeleteCustomField_WithStoredValues_ReturnsConflict()
    {
        var key = $"inuse{Guid.NewGuid():N}"[..10];
        var field = await AddCustomFieldAsync(CiType.Software, key, "In use field", "Text", isRequired: false);
        await CreateCiAsync(
            CiType.Software,
            $"Software holding a value {Guid.NewGuid():N}",
            new() { ["vendor"] = "Atlassian", ["version"] = "9.4.1" },
            customFields: new() { [key] = "held" });

        using var request = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetCi_UnknownId_ReturnsNotFoundProblem()
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/cis/{Guid.CreateVersion7()}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ListCis_AsEndUser_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/cis", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AddCustomField_AsTechnician_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/ci-custom-fields", "Technician");
        request.Content = JsonContent.Create(new
        {
            ciType = "Server",
            key = "unauthorised",
            label = "Unauthorised",
            type = "Text",
            isRequired = false,
        });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A Select custom field is how a Hardware CI says it is a laptop rather than a printer — CiType
    /// stops at "Hardware" — so the list has to be able to narrow on one.
    /// </summary>
    [Fact]
    public async Task ListCis_FiltersByACustomFieldValue()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var field = await AddSelectFieldAsync(
            CiType.Hardware, $"kind_{marker}", "Hardware type", "Laptop", "Printer");
        try
        {
            await CreateCiAsync(CiType.Hardware, $"{marker}-laptop",
                new() { ["manufacturer"] = "Dell", ["model"] = "Latitude" },
                customFields: new() { [field.Key] = "Laptop" });
            await CreateCiAsync(CiType.Hardware, $"{marker}-printer",
                new() { ["manufacturer"] = "HP", ["model"] = "LaserJet" },
                customFields: new() { [field.Key] = "Printer" });

            using var request = Authenticated(
                HttpMethod.Get, $"/api/cis?type=Hardware&search={marker}&customField={field.Id}:Laptop");
            using var response = await _client!.SendAsync(request);
            var page = await response.Content.ReadFromJsonAsync<CiPageDto>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal($"{marker}-laptop", Assert.Single(page!.Items).Name);
        }
        finally
        {
            using var cleanup = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
            await _client!.SendAsync(cleanup);
        }
    }

    /// <summary>Two Select fields AND together — narrowing by both is the point of having both.</summary>
    [Fact]
    public async Task ListCis_FiltersByTwoCustomFieldsAtOnce()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var kind = await AddSelectFieldAsync(CiType.Hardware, $"kind_{marker}", "Kind", "Laptop", "Printer");
        var estate = await AddSelectFieldAsync(CiType.Hardware, $"estate_{marker}", "Estate", "Loan", "Owned");
        try
        {
            await CreateCiAsync(CiType.Hardware, $"{marker}-loan-laptop",
                new() { ["manufacturer"] = "Dell", ["model"] = "Latitude" },
                customFields: new() { [kind.Key] = "Laptop", [estate.Key] = "Loan" });
            await CreateCiAsync(CiType.Hardware, $"{marker}-owned-laptop",
                new() { ["manufacturer"] = "Dell", ["model"] = "Latitude" },
                customFields: new() { [kind.Key] = "Laptop", [estate.Key] = "Owned" });

            using var request = Authenticated(HttpMethod.Get,
                $"/api/cis?type=Hardware&search={marker}&customField={kind.Id}:Laptop&customField={estate.Id}:Loan");
            using var response = await _client!.SendAsync(request);
            var page = await response.Content.ReadFromJsonAsync<CiPageDto>();

            Assert.Equal($"{marker}-loan-laptop", Assert.Single(page!.Items).Name);
        }
        finally
        {
            foreach (var field in new[] { kind, estate })
            {
                using var cleanup = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
                await _client!.SendAsync(cleanup);
            }
        }
    }

    /// <summary>A CI that has never been given a value for the field is not a match for any value.</summary>
    [Fact]
    public async Task ListCis_FilteringByACustomField_ExcludesCisThatHaveNoValueForIt()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var field = await AddSelectFieldAsync(CiType.Hardware, $"kind_{marker}", "Kind", "Laptop");
        try
        {
            await CreateCiAsync(CiType.Hardware, $"{marker}-untagged",
                new() { ["manufacturer"] = "Dell", ["model"] = "Latitude" });

            using var request = Authenticated(
                HttpMethod.Get, $"/api/cis?type=Hardware&search={marker}&customField={field.Id}:Laptop");
            using var response = await _client!.SendAsync(request);
            var page = await response.Content.ReadFromJsonAsync<CiPageDto>();

            Assert.Empty(page!.Items);
        }
        finally
        {
            using var cleanup = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
            await _client!.SendAsync(cleanup);
        }
    }

    /// <summary>FAILURE PATH: a malformed filter is a 400, not a silently ignored parameter.</summary>
    [Theory]
    [InlineData("not-a-guid:Laptop")]
    [InlineData("Laptop")]
    [InlineData(":Laptop")]
    public async Task ListCis_WithAMalformedCustomFieldFilter_ReturnsBadRequest(string token)
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/cis?customField={token}");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Adding an option strands nothing: values are stored against the field's id, not its text, so
    /// everything already recorded keeps its meaning. This is the edit people actually want.
    /// </summary>
    [Fact]
    public async Task UpdateCustomField_AddingAnOption_IsAllowedAndKeepsExistingValues()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var field = await AddSelectFieldAsync(CiType.Hardware, $"kind_{marker}", "Kind", "Laptop");
        try
        {
            var ci = await CreateCiAsync(CiType.Hardware, $"{marker}-laptop",
                new() { ["manufacturer"] = "Dell", ["model"] = "Latitude" },
                customFields: new() { [field.Key] = "Laptop" });

            using var request = Authenticated(HttpMethod.Put, $"/api/ci-custom-fields/{field.Id}", "Admin");
            request.Content = JsonContent.Create(new
            {
                label = "Hardware kind",
                isRequired = false,
                options = new[] { "Laptop", "Desktop", "Printer" },
                sortOrder = 0,
            });
            using var response = await _client!.SendAsync(request);
            var updated = await response.Content.ReadFromJsonAsync<CiCustomFieldDto>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Hardware kind", updated!.Label);
            Assert.Equal(["Laptop", "Desktop", "Printer"], updated.Options);

            // The CI that was already a Laptop is untouched and still says so.
            using var read = Authenticated(HttpMethod.Get, $"/api/cis/{ci.Id}");
            using var readResponse = await _client.SendAsync(read);
            var fetched = await readResponse.Content.ReadFromJsonAsync<CiDto>();
            Assert.Equal("Laptop", fetched!.CustomFields.Single(value => value.Key == field.Key).Value);
        }
        finally
        {
            using var cleanup = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
            await _client!.SendAsync(cleanup);
        }
    }

    /// <summary>
    /// FAILURE PATH: removing an option that CIs still hold would leave them failing validation on
    /// their next edit, for a field nobody touched. Refused, and the refusal says how many.
    /// </summary>
    [Fact]
    public async Task UpdateCustomField_RemovingAnOptionCisStillHold_IsRefused()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var field = await AddSelectFieldAsync(CiType.Hardware, $"kind_{marker}", "Kind", "Laptop", "Printer");
        try
        {
            foreach (var name in new[] { "printer-a", "printer-b" })
            {
                await CreateCiAsync(CiType.Hardware, $"{marker}-{name}",
                    new() { ["manufacturer"] = "HP", ["model"] = "LaserJet" },
                    customFields: new() { [field.Key] = "Printer" });
            }

            using var request = Authenticated(HttpMethod.Put, $"/api/ci-custom-fields/{field.Id}", "Admin");
            request.Content = JsonContent.Create(new
            {
                label = "Kind",
                isRequired = false,
                options = new[] { "Laptop" },
                sortOrder = 0,
            });
            using var response = await _client!.SendAsync(request);
            var problem = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("2 on 'Printer'", problem, StringComparison.Ordinal);
        }
        finally
        {
            using var cleanup = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
            await _client!.SendAsync(cleanup);
        }
    }

    /// <summary>An option nothing is recorded as is free to go — the guard is about data, not history.</summary>
    [Fact]
    public async Task UpdateCustomField_RemovingAnUnusedOption_IsAllowed()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var field = await AddSelectFieldAsync(CiType.Hardware, $"kind_{marker}", "Kind", "Laptop", "Fax");
        try
        {
            using var request = Authenticated(HttpMethod.Put, $"/api/ci-custom-fields/{field.Id}", "Admin");
            request.Content = JsonContent.Create(new
            {
                label = "Kind",
                isRequired = false,
                options = new[] { "Laptop" },
                sortOrder = 0,
            });
            using var response = await _client!.SendAsync(request);
            var updated = await response.Content.ReadFromJsonAsync<CiCustomFieldDto>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["Laptop"], updated!.Options);
        }
        finally
        {
            using var cleanup = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
            await _client!.SendAsync(cleanup);
        }
    }

    /// <summary>The counts are what let the dialog show what is removable before anybody clicks.</summary>
    [Fact]
    public async Task GetCustomFieldValueCounts_ReportsHowManyCisHoldEachOption()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var field = await AddSelectFieldAsync(CiType.Hardware, $"kind_{marker}", "Kind", "Laptop", "Printer", "Fax");
        try
        {
            await CreateCiAsync(CiType.Hardware, $"{marker}-a",
                new() { ["manufacturer"] = "Dell", ["model"] = "Latitude" },
                customFields: new() { [field.Key] = "Laptop" });
            await CreateCiAsync(CiType.Hardware, $"{marker}-b",
                new() { ["manufacturer"] = "Dell", ["model"] = "Latitude" },
                customFields: new() { [field.Key] = "Laptop" });
            await CreateCiAsync(CiType.Hardware, $"{marker}-c",
                new() { ["manufacturer"] = "HP", ["model"] = "LaserJet" },
                customFields: new() { [field.Key] = "Printer" });

            using var request = Authenticated(
                HttpMethod.Get, $"/api/ci-custom-fields/{field.Id}/value-counts", "Admin");
            using var response = await _client!.SendAsync(request);
            var counts = Assert.IsType<List<CiCustomFieldValueCountDto>>(
                await response.Content.ReadFromJsonAsync<List<CiCustomFieldValueCountDto>>());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, counts.Single(count => count.Value == "Laptop").CiCount);
            Assert.Equal(1, counts.Single(count => count.Value == "Printer").CiCount);
            // An option nothing is recorded as simply does not appear.
            Assert.DoesNotContain(counts, count => count.Value == "Fax");
        }
        finally
        {
            using var cleanup = Authenticated(HttpMethod.Delete, $"/api/ci-custom-fields/{field.Id}", "Admin");
            await _client!.SendAsync(cleanup);
        }
    }

    /// <summary>FAILURE PATH: editing fields is administration, not a technician's job.</summary>
    [Fact]
    public async Task UpdateCustomField_AsATechnician_IsForbidden()
    {
        using var request = Authenticated(
            HttpMethod.Put, $"/api/ci-custom-fields/{Guid.NewGuid()}", "Technician");
        request.Content = JsonContent.Create(new { label = "Nope", isRequired = false, sortOrder = 0 });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<CiDto> CreateCiAsync(
        CiType type,
        string name,
        Dictionary<string, string?> attributes,
        string? assetTag = null,
        Dictionary<string, string?>? customFields = null)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = type.ToString(),
            name,
            assetTag,
            attributes,
            customFields,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private async Task<CiCustomFieldDto> AddCustomFieldAsync(
        CiType ciType,
        string key,
        string label,
        string type,
        bool isRequired)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/ci-custom-fields", "Admin");
        request.Content = JsonContent.Create(new
        {
            ciType = ciType.ToString(),
            key,
            label,
            type,
            isRequired,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiCustomFieldDto>(await response.Content.ReadFromJsonAsync<CiCustomFieldDto>());
    }

    private async Task<CiCustomFieldDto> AddSelectFieldAsync(
        CiType ciType,
        string key,
        string label,
        params string[] options)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/ci-custom-fields", "Admin");
        request.Content = JsonContent.Create(new
        {
            ciType = ciType.ToString(),
            key,
            label,
            type = "Select",
            isRequired = false,
            options,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiCustomFieldDto>(await response.Content.ReadFromJsonAsync<CiCustomFieldDto>());
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(CiAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(
        Guid Id,
        string Type,
        string Name,
        string? AssetTag,
        string? SerialNumber,
        string? Description,
        bool IsActive,
        Dictionary<string, string> Attributes,
        List<CiCustomFieldValueDto> CustomFields);

    private sealed record CiCustomFieldValueDto(Guid FieldId, string Key, string Label, string Type, string Value);

    private sealed record CiCustomFieldValueCountDto(string Value, int CiCount);

    private sealed record CiPageDto(List<CiDto> Items, int Total, int Page, int PageSize);

    private sealed record CiCustomFieldDto(
        Guid Id, string CiType, string Key, string Label, string Type, bool IsRequired,
        List<string> Options);

    private sealed record CiAttributeDto(string Key, string Label, string Kind, bool IsRequired);

    private sealed record CiTypeSchemaDto(
        string Type,
        List<CiAttributeDto> Attributes,
        List<CiCustomFieldDto> CustomFields);

    private sealed class CiApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public CiApplication(string connectionString, string rabbitMqConnectionString, string minioConnectionString)
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
                        options.DefaultAuthenticateScheme = CiAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = CiAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = CiAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, CiAuthenticationHandler>(
                        CiAuthenticationHandler.TestScheme,
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

    private sealed class CiAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "CiTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "ci-test-user-id"),
                    new Claim(ClaimTypes.Name, "ci-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
