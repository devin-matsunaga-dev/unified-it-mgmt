using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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

using Modules.Monitoring.Data;
using Platform.Data;
using Platform.Vault;

namespace Infrastructure.Tests;

/// <summary>
/// The credential vault (WP-3.11), and specifically the property the whole package exists for:
/// <b>no read ever returns secret material</b>. Every test here that touches a secret checks for the
/// literal string in the response, in the list, in the audit entry and in the stored row.
/// <para>
/// The poller-facing half — scope, grant, redemption — is exercised end to end, because "the poller
/// polls successfully using a vaulted credential" is a claim about four components agreeing and
/// nothing smaller than this proves it.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CredentialVaultApiIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Distinctive enough that finding it anywhere is unambiguous.</summary>
    private const string Secret = "c0mmun1ty-must-never-appear";

    private readonly VaultApplication _application;
    private HttpClient _admin = null!;
    private HttpClient _technician = null!;
    private HttpClient _poller = null!;
    private string _suffix = null!;

    public CredentialVaultApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new VaultApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _admin = Client("Admin");
        _technician = Client("Technician");
        _poller = Client("Poller");
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        // The grant endpoints read `monitoring.check_definitions` through this module's own context,
        // and the vault's delete guard reads it through a port — a host that never migrated the
        // monitoring schema answers 500 rather than failing in DI. That is the WP-3.6 port trap, and
        // this is the fourth package to need the note.
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
        // Credential names are unique platform-wide against a database the whole collection shares.
        // Version 4 deliberately: a v7 GUID opens with a millisecond timestamp and would be identical
        // for every test in the run (the WP-3.10 note).
        _suffix = Guid.NewGuid().ToString("N")[..8];
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>ARCHITECTURE §7.3: the material is write-only. This is that invariant, four ways.</summary>
    [Fact]
    public async Task Credential_Created_NeverReturnsItsSecretAndStoresItAsCiphertext()
    {
        var created = await CreateAsync("snmp-write-only");

        // 1. Not in the create response, which carries the field names and not their values.
        Assert.Equal(["community"], created.Fields);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(created, Json), StringComparison.Ordinal);

        // 2. Not in a list, and not in a read by id.
        Assert.DoesNotContain(
            Secret, await _admin.GetStringAsync("/api/credentials"), StringComparison.Ordinal);
        Assert.DoesNotContain(
            Secret, await _admin.GetStringAsync($"/api/credentials/{created.Id}"), StringComparison.Ordinal);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // 3. Not in the audit entry — an audit log that records the secret hands it to everyone who
        // can read the log, which is the WP-3.10 webhook rule applied to the real thing.
        var audit = await dbContext.AuditEntries.AsNoTracking()
            .SingleAsync(entry => entry.EntityType == "Credential" && entry.EntityId == created.Id.ToString());
        Assert.Equal("Created", audit.Action);
        Assert.DoesNotContain(Secret, audit.AfterJson!, StringComparison.Ordinal);

        // 4. Encrypted at rest. The row holds a protected blob and not the plaintext.
        var stored = await dbContext.Credentials.AsNoTracking().SingleAsync(item => item.Id == created.Id);
        Assert.DoesNotContain(Secret, stored.SecretCipher, StringComparison.Ordinal);
        Assert.NotEmpty(stored.SecretCipher);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task Credential_WithMaterialTheKindDoesNotUnderstand_IsRefused()
    {
        var response = await _admin.PostAsJsonAsync("/api/credentials", new
        {
            name = $"snmp-bad-field-{_suffix}",
            kind = "SnmpV2c",
            material = new Dictionary<string, string> { ["privateKey"] = Secret },
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        Assert.Contains("Material", problem!.Errors.Keys);
        // Even a refusal must not echo the value back.
        Assert.DoesNotContain(
            Secret, JsonSerializer.Serialize(problem, Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_WithADuplicateName_IsRefused()
    {
        var created = await CreateAsync("snmp-duplicate");

        var response = await _admin.PostAsJsonAsync("/api/credentials", new
        {
            name = created.Name,
            kind = "SnmpV2c",
            material = new Dictionary<string, string> { ["community"] = "other" },
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// A rotation replaces the secret and moves the version, which is the entire mechanism behind
    /// "rotate the credential and the poller picks it up next cycle".
    /// </summary>
    [Fact]
    public async Task Credential_Rotated_MovesItsVersionAndReplacesTheStoredCiphertext()
    {
        var created = await CreateAsync("snmp-rotate");
        await using var before = _application.Services.CreateAsyncScope();
        var firstCipher = (await before.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .Credentials.AsNoTracking().SingleAsync(item => item.Id == created.Id)).SecretCipher;

        var response = await _admin.PostAsJsonAsync(
            $"/api/credentials/{created.Id}/rotations",
            new { material = new Dictionary<string, string> { ["community"] = "rotated-value" } });

        response.EnsureSuccessStatusCode();
        var rotated = (await response.Content.ReadFromJsonAsync<CredentialResponse>(Json))!;
        Assert.Equal(2, rotated.Version);
        Assert.True(rotated.RotatedAt > created.RotatedAt || rotated.Version > created.Version);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stored = await dbContext.Credentials.AsNoTracking().SingleAsync(item => item.Id == created.Id);
        Assert.NotEqual(firstCipher, stored.SecretCipher);
        Assert.DoesNotContain("rotated-value", stored.SecretCipher, StringComparison.Ordinal);

        // "Rotated" rather than "Updated": what changed is not visible in the before/after pair,
        // because neither of them may carry the secret, so the action name is the record of it.
        var actions = await dbContext.AuditEntries.AsNoTracking()
            .Where(entry => entry.EntityType == "Credential" && entry.EntityId == created.Id.ToString())
            .Select(entry => entry.Action)
            .ToListAsync();
        Assert.Contains("Rotated", actions);
    }

    /// <summary>
    /// The delete guard, asked through <c>ICredentialUsageDirectory</c> because Platform may not
    /// query a monitoring table. Deleting a credential a check names would leave the check
    /// authenticating with nothing from the next cycle, silently.
    /// </summary>
    [Fact]
    public async Task Credential_UsedByACheck_CannotBeDeleted()
    {
        var created = await CreateAsync("snmp-in-use");
        await SeedCheckAsync(created.Id);

        var response = await _admin.DeleteAsync($"/api/credentials/{created.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.Contains("check", problem!.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Credential_UsedByNothing_IsDeletedAndAudited()
    {
        var created = await CreateAsync("snmp-unused");

        var response = await _admin.DeleteAsync($"/api/credentials/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(await dbContext.Credentials.AnyAsync(item => item.Id == created.Id));
        Assert.Contains("Deleted", await dbContext.AuditEntries.AsNoTracking()
            .Where(entry => entry.EntityType == "Credential" && entry.EntityId == created.Id.ToString())
            .Select(entry => entry.Action).ToListAsync());
    }

    /// <summary>
    /// The whole poller path: scope → grant → redemption → material, with the access audited.
    /// This is the WP's "poller polls successfully using vaulted cred" and "access appears in audit
    /// log" in one, minus the socket.
    /// </summary>
    [Fact]
    public async Task Poller_RedeemsAGrantForItsOwnScope_AndTheAccessIsAudited()
    {
        var created = await CreateAsync("snmp-granted");
        var pollerName = await SeedPollerAndCheckAsync(created.Id);

        var scopeResponse = await _poller.GetFromJsonAsync<PollerCredentialScope>(
            $"/api/pollers/{pollerName}/credentials", Json);
        var descriptor = Assert.Single(scopeResponse!.Credentials);
        Assert.Equal(created.Id, descriptor.Id);
        Assert.Equal(1, descriptor.Version);
        // The scope is metadata. It is fetched every cycle, so it must never carry material.
        Assert.DoesNotContain(
            Secret, JsonSerializer.Serialize(scopeResponse, Json), StringComparison.Ordinal);

        var grant = await PostAsync<CredentialGrantResponse>(
            _poller, $"/api/pollers/{pollerName}/credential-grants");
        Assert.NotEqual(Guid.Empty, grant.GrantId);
        Assert.NotEmpty(grant.Token);

        var redemption = await _poller.PostAsJsonAsync(
            "/api/credential-grants/redemptions", new { grantId = grant.GrantId, token = grant.Token });
        redemption.EnsureSuccessStatusCode();
        var released = (await redemption.Content.ReadFromJsonAsync<RedeemCredentialGrantResponse>(Json))!;

        // The one place in the platform where the plaintext legitimately appears.
        var credential = Assert.Single(released.Credentials);
        Assert.Equal(Secret, credential.Material["community"]);
        Assert.Equal(1, credential.Version);

        await using var databaseScope = _application.Services.CreateAsyncScope();
        var dbContext = databaseScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var access = await dbContext.AuditEntries.AsNoTracking()
            .SingleAsync(entry => entry.EntityType == "Credential"
                && entry.EntityId == created.Id.ToString()
                && entry.Action == "Accessed");
        Assert.Contains(grant.GrantId.ToString(), access.AfterJson!, StringComparison.OrdinalIgnoreCase);
        // The access entry states what was read, never its value.
        Assert.DoesNotContain(Secret, access.AfterJson!, StringComparison.Ordinal);

        // And the credential records that somebody read it, without anybody having to read a log.
        var stored = await dbContext.Credentials.AsNoTracking().SingleAsync(item => item.Id == created.Id);
        Assert.NotNull(stored.LastAccessedAt);
    }

    /// <summary>A grant is a two-minute permission slip. Spending it twice is a 409, not a repeat.</summary>
    [Fact]
    public async Task Grant_RedeemedTwice_IsRefusedTheSecondTime()
    {
        var created = await CreateAsync("snmp-single-use");
        var pollerName = await SeedPollerAndCheckAsync(created.Id);
        var grant = await PostAsync<CredentialGrantResponse>(
            _poller, $"/api/pollers/{pollerName}/credential-grants");

        var first = await _poller.PostAsJsonAsync(
            "/api/credential-grants/redemptions", new { grantId = grant.GrantId, token = grant.Token });
        var second = await _poller.PostAsJsonAsync(
            "/api/credential-grants/redemptions", new { grantId = grant.GrantId, token = grant.Token });

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    /// <summary>
    /// The token is the secret, not the id. A caller holding a real grant id and a wrong token gets
    /// the same answer as one holding neither — distinguishing them would make this an oracle.
    /// </summary>
    [Fact]
    public async Task Grant_RedeemedWithTheWrongToken_IsRefusedIndistinguishablyFromAnUnknownGrant()
    {
        var created = await CreateAsync("snmp-wrong-token");
        var pollerName = await SeedPollerAndCheckAsync(created.Id);
        var grant = await PostAsync<CredentialGrantResponse>(
            _poller, $"/api/pollers/{pollerName}/credential-grants");

        var wrongToken = await _poller.PostAsJsonAsync(
            "/api/credential-grants/redemptions",
            new { grantId = grant.GrantId, token = "not-the-token" });
        var unknownGrant = await _poller.PostAsJsonAsync(
            "/api/credential-grants/redemptions",
            new { grantId = Guid.NewGuid(), token = "not-the-token" });

        Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownGrant.StatusCode);
        Assert.Equal(
            (await wrongToken.Content.ReadFromJsonAsync<ProblemDetailsBody>())!.Title,
            (await unknownGrant.Content.ReadFromJsonAsync<ProblemDetailsBody>())!.Title);
    }

    /// <summary>
    /// The token is stored as a hash, so the row itself is not redeemable — a stolen database hands
    /// somebody grants they cannot spend.
    /// </summary>
    [Fact]
    public async Task Grant_StoresOnlyAHashOfItsToken()
    {
        var created = await CreateAsync("snmp-token-hash");
        var pollerName = await SeedPollerAndCheckAsync(created.Id);

        var grant = await PostAsync<CredentialGrantResponse>(
            _poller, $"/api/pollers/{pollerName}/credential-grants");

        await using var scope = _application.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .CredentialGrants.AsNoTracking().SingleAsync(item => item.Id == grant.GrantId);
        Assert.NotEqual(grant.Token, stored.TokenHash);
        Assert.DoesNotContain(grant.Token, stored.TokenHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expired grant is refused. Written against the row rather than by waiting, because the
    /// lifetime is two minutes and a test that slept for it would be a two-minute test.
    /// </summary>
    [Fact]
    public async Task Grant_ThatHasExpired_IsRefused()
    {
        var created = await CreateAsync("snmp-expired");
        var pollerName = await SeedPollerAndCheckAsync(created.Id);
        var grant = await PostAsync<CredentialGrantResponse>(
            _poller, $"/api/pollers/{pollerName}/credential-grants");

        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = await dbContext.CredentialGrants.SingleAsync(item => item.Id == grant.GrantId);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await dbContext.SaveChangesAsync();
        }

        var response = await _poller.PostAsJsonAsync(
            "/api/credential-grants/redemptions", new { grantId = grant.GrantId, token = grant.Token });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// A poller's scope is derived from its own devices, never from what it asks for — so a grant
    /// covers exactly the credentials that poller's checks name and nothing else in the vault.
    /// </summary>
    [Fact]
    public async Task PollerScope_CoversOnlyTheCredentialsItsOwnChecksName()
    {
        var mine = await CreateAsync("snmp-mine");
        var someoneElses = await CreateAsync("snmp-theirs");
        var pollerName = await SeedPollerAndCheckAsync(mine.Id);

        var scope = await _poller.GetFromJsonAsync<PollerCredentialScope>(
            $"/api/pollers/{pollerName}/credentials", Json);

        Assert.Equal([mine.Id], scope!.Credentials.Select(descriptor => descriptor.Id));
        Assert.DoesNotContain(scope.Credentials, descriptor => descriptor.Id == someoneElses.Id);
    }

    /// <summary>A deactivated credential is never released, which is what makes it a kill switch.</summary>
    [Fact]
    public async Task Credential_Deactivated_LeavesThePollersScope()
    {
        var created = await CreateAsync("snmp-deactivated");
        var pollerName = await SeedPollerAndCheckAsync(created.Id);

        var update = await _admin.PutAsJsonAsync($"/api/credentials/{created.Id}", new
        {
            name = created.Name,
            siteId = (Guid?)null,
            description = (string?)null,
            isActive = false,
        });
        update.EnsureSuccessStatusCode();

        var scope = await _poller.GetFromJsonAsync<PollerCredentialScope>(
            $"/api/pollers/{pollerName}/credentials", Json);
        Assert.Empty(scope!.Credentials);
    }

    /// <summary>
    /// Managing credentials is administration, not monitoring: a technician who can edit a check
    /// must not thereby be able to replace the secret it authenticates with.
    /// </summary>
    [Theory]
    [InlineData("Technician")]
    [InlineData("Manager")]
    [InlineData("Poller")]
    public async Task CredentialSurface_IsRefusedToEverybodyButAnAdmin(string role)
    {
        using var client = Client(role);

        var response = await client.GetAsync("/api/credentials");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// And the reverse, which is the half that is easy to forget: <c>CanPoll</c> is the Poller realm
    /// role and nothing else, so there is no operator path to material at all — not even an Admin's.
    /// </summary>
    [Theory]
    [InlineData("Admin")]
    [InlineData("Technician")]
    public async Task Redemption_IsRefusedToAnOperator(string role)
    {
        using var client = Client(role);

        var response = await client.PostAsJsonAsync(
            "/api/credential-grants/redemptions", new { grantId = Guid.NewGuid(), token = "anything" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CredentialSurface_IsRefusedToAnAnonymousCaller()
    {
        using var anonymous = _application.CreateClient();

        var response = await anonymous.GetAsync("/api/credentials");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient Client(string role)
    {
        var client = _application.CreateClient();
        client.DefaultRequestHeaders.Add(VaultAuthenticationHandler.RoleHeader, role);
        return client;
    }

    private async Task<CredentialResponse> CreateAsync(string name)
    {
        var response = await _admin.PostAsJsonAsync("/api/credentials", new
        {
            name = $"{name}-{_suffix}",
            kind = "SnmpV2c",
            material = new Dictionary<string, string> { ["community"] = Secret },
            siteId = (Guid?)null,
            description = "Seeded by an integration test.",
            isActive = true,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CredentialResponse>(Json))!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path)
    {
        var response = await client.PostAsync(path, null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    /// <summary>
    /// A device and an SNMP check naming this credential, written straight to the context. The check
    /// API is exercised by <see cref="MonitoringConfigApiIntegrationTests"/>; what these tests need
    /// is a row for the scope query and the delete guard to find.
    /// </summary>
    private async Task<Guid> SeedCheckAsync(Guid credentialId, string? pollerGroup = null)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var now = DateTimeOffset.UtcNow;
        var device = new MonitoredDevice
        {
            Id = Guid.CreateVersion7(),
            CiId = Guid.NewGuid(),
            Address = "snmpsim",
            PollerGroup = pollerGroup ?? $"vault-{_suffix}",
            IsEnabled = true,
            CreatedBy = "vault-test",
            CreatedAt = now,
            UpdatedBy = "vault-test",
            UpdatedAt = now,
            Checks =
            [
                new CheckDefinition
                {
                    Id = Guid.CreateVersion7(),
                    Type = CheckType.Snmp,
                    Name = $"SNMP: CPU {_suffix}",
                    IntervalSeconds = 60,
                    TimeoutSeconds = 5,
                    Comparison = ThresholdComparison.GreaterThan,
                    ParametersJson = """{"metric":"cpu","version":"2c"}""",
                    CredentialId = credentialId,
                    IsEnabled = true,
                    CreatedBy = "vault-test",
                    CreatedAt = now,
                    UpdatedBy = "vault-test",
                    UpdatedAt = now,
                },
            ],
        };
        dbContext.MonitoredDevices.Add(device);
        await dbContext.SaveChangesAsync();
        return device.Id;
    }

    /// <summary>
    /// A poller and a device in its own group. The group is per test — routing rules were global in
    /// WP-3.10 and one test's fixture answered another's query; a poller group is the same trap, and
    /// the suffix is what keeps each scope query answering about its own rows.
    /// </summary>
    private async Task<string> SeedPollerAndCheckAsync(Guid credentialId)
    {
        var group = $"vault-{Guid.NewGuid():N}"[..20];
        var name = $"poller-{group}";
        await SeedCheckAsync(credentialId, group);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        dbContext.Pollers.Add(new Poller
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            PollerGroup = group,
            RegisteredAt = DateTimeOffset.UtcNow,
            LastRegisteredAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return name;
    }

    private sealed record PollerCredentialScope(
        string PollerName, string PollerGroup, IReadOnlyList<CredentialDescriptor> Credentials);

    private sealed record ValidationProblem(IDictionary<string, string[]> Errors);

    private sealed record ProblemDetailsBody(string? Title, string? Detail);

    private sealed class VaultApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public VaultApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
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
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = VaultAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = VaultAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = VaultAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, VaultAuthenticationHandler>(
                        VaultAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
        }
    }

    private sealed class VaultAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "VaultTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].ToString();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, $"vault-test-{role}"),
                    new Claim(ClaimTypes.Name, $"vault-test-{role}"),
                    new Claim(ClaimTypes.Role, role),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
