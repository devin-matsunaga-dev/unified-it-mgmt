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

using Platform.Data;
using Platform.Notifications;

namespace Infrastructure.Tests;

/// <summary>
/// The notification configuration surface: who may reach it, what a read is allowed to answer with,
/// and which writes are refused. The routing engine itself is covered by
/// <see cref="NotificationRoutingIntegrationTests"/>.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class NotificationApiIntegrationTests : IAsyncLifetime
{
    // The host writes enums as their names (Program.cs configures JsonStringEnumConverter), so a
    // client reading with the defaults sees "Warning" where it expects a number.
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string WebhookUrl = "https://contoso.webhook.office.com/webhookb2/abc/IncomingWebhook/s3cr3t/zzzz";

    private readonly NotificationApplication _application;
    private HttpClient _admin = null!;
    private HttpClient _technician = null!;
    private string _suffix = null!;

    public NotificationApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new NotificationApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _admin = _application.CreateClient();
        _admin.DefaultRequestHeaders.Add(NotificationAuthenticationHandler.RoleHeader, "Admin");
        _technician = _application.CreateClient();
        _technician.DefaultRequestHeaders.Add(NotificationAuthenticationHandler.RoleHeader, "Technician");
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        // Channel and rule names are unique platform-wide against a database the whole collection
        // shares. Version 4 deliberately: a v7 GUID's leading characters are a timestamp and would be
        // identical for every test in the run.
        _suffix = Guid.NewGuid().ToString("N")[..8];
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// ARCHITECTURE §7's vault rule applied to a webhook URL: it is a bearer credential, so no read
    /// ever returns it — not the create response, not the list, and not the audit entry either.
    /// </summary>
    [Fact]
    public async Task Channel_Created_NeverReturnsItsWebhookUrl()
    {
        var created = await CreateChannelAsync("chat", NotificationChannelKind.Teams, WebhookUrl);

        Assert.Equal("https://contoso.webhook.office.com/…zzzz", created.TargetRedacted);

        var listed = await _admin.GetStringAsync("/api/notification-channels");
        Assert.DoesNotContain("s3cr3t", listed, StringComparison.Ordinal);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audit = await dbContext.AuditEntries.AsNoTracking()
            .Where(entry => entry.EntityType == "NotificationChannel" && entry.EntityId == created.Id.ToString())
            .SingleAsync();
        Assert.Equal("Created", audit.Action);
        Assert.DoesNotContain("s3cr3t", audit.AfterJson!, StringComparison.Ordinal);
        // The value is stored, though — it has to be, or nothing could post to it.
        var channel = await dbContext.NotificationChannels.AsNoTracking().SingleAsync(item => item.Id == created.Id);
        Assert.Equal(WebhookUrl, channel.Target);
    }

    /// <summary>
    /// An edit has to be possible without the client knowing a value no read has ever returned it, so
    /// an omitted target leaves the stored one alone.
    /// </summary>
    [Fact]
    public async Task Channel_UpdatedWithoutATarget_KeepsTheStoredOne()
    {
        var created = await CreateChannelAsync("keeps-target", NotificationChannelKind.Teams, WebhookUrl);

        var response = await _admin.PutAsJsonAsync($"/api/notification-channels/{created.Id}",
            new { name = created.Name, target = (string?)null, description = "Renamed", isActive = false });

        response.EnsureSuccessStatusCode();
        await using var scope = _application.Services.CreateAsyncScope();
        var channel = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .NotificationChannels.AsNoTracking().SingleAsync(item => item.Id == created.Id);
        Assert.Equal(WebhookUrl, channel.Target);
        Assert.False(channel.IsActive);
    }

    /// <summary>Failure path: a webhook channel pointed at something that is not a webhook.</summary>
    [Fact]
    public async Task Channel_CreatedWithAnEmailAsAWebhook_IsRefusedWithAFieldError()
    {
        var response = await _admin.PostAsJsonAsync("/api/notification-channels", new
        {
            name = $"bad-{_suffix}",
            kind = "Teams",
            target = "oncall@it-platform.local",
            description = (string?)null,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        Assert.Contains("Target", problem!.Errors.Keys);
    }

    /// <summary>
    /// Deleting a channel that rules still send to is how a Critical alert quietly stops reaching
    /// anybody. Same guard as the WP-2.6 contract and vendor deletes.
    /// </summary>
    [Fact]
    public async Task Channel_DeletedWhileARuleSendsToIt_IsRefused()
    {
        var channel = await CreateChannelAsync("in-use", NotificationChannelKind.Email, "ops@it-platform.local");
        await CreateRuleAsync("uses-it", channel.Id, NotificationSeverity.Warning);

        var refused = await _admin.DeleteAsync($"/api/notification-channels/{channel.Id}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.Contains("routing rule", problem!.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Failure path: half a quiet-hours window is not a window.</summary>
    [Fact]
    public async Task Rule_WithOnlyOneEndOfItsQuietHours_IsRefused()
    {
        var channel = await CreateChannelAsync("half-window", NotificationChannelKind.Email, "ops@it-platform.local");

        var response = await _admin.PostAsJsonAsync("/api/notification-routing-rules", new
        {
            name = $"half-{_suffix}",
            channelId = channel.Id,
            eventKind = (string?)null,
            minimumSeverity = "Warning",
            deviceGroup = (string?)null,
            quietHoursStart = "22:00:00",
            quietHoursEnd = (string?)null,
            timeZone = "UTC",
            digestQuietHours = true,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        Assert.Contains("QuietHoursStart", problem!.Errors.Keys);
    }

    /// <summary>Failure path: an operator-entered time zone that does not exist.</summary>
    [Fact]
    public async Task Rule_WithAnUnknownTimeZone_IsRefusedByName()
    {
        var channel = await CreateChannelAsync("bad-zone", NotificationChannelKind.Email, "ops@it-platform.local");

        var response = await _admin.PostAsJsonAsync("/api/notification-routing-rules", new
        {
            name = $"zone-{_suffix}",
            channelId = channel.Id,
            eventKind = (string?)null,
            minimumSeverity = "Warning",
            deviceGroup = (string?)null,
            quietHoursStart = (string?)null,
            quietHoursEnd = (string?)null,
            timeZone = "Middle/Earth",
            digestQuietHours = true,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        Assert.Contains("TimeZone", problem!.Errors.Keys);
    }

    [Fact]
    public async Task Rule_PointingAtNoChannel_IsRefused()
    {
        var response = await _admin.PostAsJsonAsync("/api/notification-routing-rules", new
        {
            name = $"orphan-{_suffix}",
            channelId = Guid.CreateVersion7(),
            eventKind = (string?)null,
            minimumSeverity = "Warning",
            deviceGroup = (string?)null,
            quietHoursStart = (string?)null,
            quietHoursEnd = (string?)null,
            timeZone = "UTC",
            digestQuietHours = true,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        Assert.Contains("ChannelId", problem!.Errors.Keys);
    }

    /// <summary>
    /// A preference nobody has saved answers with the permissive default and says so — a screen that
    /// could not tell the two apart would show a choice the user never made as one they did.
    /// </summary>
    [Fact]
    public async Task Preference_NeverSaved_AnswersTheDefaultAndSaysItIsNotConfigured()
    {
        var preference = await _technician.GetFromJsonAsync<UserNotificationPreferenceResponse>(
            "/api/notification-preferences/me", Json);

        Assert.False(preference!.IsConfigured);
        Assert.True(preference.EmailEnabled);
        Assert.Equal(NotificationSeverity.Informational, preference.MinimumSeverity);
        Assert.Null(preference.UpdatedAt);
    }

    /// <summary>
    /// Preferences are not an admin surface: everybody who can sign in has notifications, which is why
    /// a Technician reaches this and not the channels beside it.
    /// </summary>
    [Fact]
    public async Task Preference_SavedByANonAdmin_IsStoredAndReadBack()
    {
        var saved = await _technician.PutAsJsonAsync("/api/notification-preferences/me", new
        {
            emailAddress = "pager@it-platform.local",
            emailEnabled = true,
            minimumSeverity = "Critical",
            quietHoursStart = "22:00:00",
            quietHoursEnd = "07:00:00",
            timeZone = "Europe/London",
            digestQuietHours = true,
        });

        saved.EnsureSuccessStatusCode();
        var preference = await _technician.GetFromJsonAsync<UserNotificationPreferenceResponse>(
            "/api/notification-preferences/me", Json);
        Assert.True(preference!.IsConfigured);
        Assert.Equal("pager@it-platform.local", preference.EmailAddress);
        Assert.Equal(NotificationSeverity.Critical, preference.MinimumSeverity);
        Assert.Equal(new TimeOnly(22, 0), preference.QuietHoursStart);
        Assert.Equal("Europe/London", preference.TimeZone);
    }

    [Fact]
    public async Task Preference_WithAnAddressThatIsNotOne_IsRefused()
    {
        var response = await _technician.PutAsJsonAsync("/api/notification-preferences/me", new
        {
            emailAddress = "not an address",
            emailEnabled = true,
            minimumSeverity = "Warning",
            quietHoursStart = (string?)null,
            quietHoursEnd = (string?)null,
            timeZone = "UTC",
            digestQuietHours = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        Assert.Contains("EmailAddress", problem!.Errors.Keys);
    }

    /// <summary>
    /// A routing rule decides who is woken at three in the morning, so the configuration surface is
    /// AdminOnly — a Technician gets 403 rather than a filtered list.
    /// </summary>
    [Theory]
    [InlineData("/api/notification-channels")]
    [InlineData("/api/notification-routing-rules")]
    [InlineData("/api/notification-deliveries")]
    public async Task ConfigurationSurfaces_AreRefusedToANonAdmin(string path)
    {
        var response = await _technician.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ConfigurationSurfaces_AreRefusedToAnAnonymousCaller()
    {
        using var anonymous = _application.CreateClient();

        var response = await anonymous.GetAsync("/api/notification-channels");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The delivery history is the durable record a run's behaviour is read from.</summary>
    [Fact]
    public async Task Deliveries_AfterRouting_AreListedNewestFirstAndFilterable()
    {
        var channel = await CreateChannelAsync("history", NotificationChannelKind.Email, "history@it-platform.local");
        var eventKind = $"TestEvent-{_suffix}";
        await CreateRuleAsync("history-rule", channel.Id, NotificationSeverity.Warning, eventKind);

        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var router = scope.ServiceProvider.GetRequiredService<INotificationRouter>();
            await router.RouteAsync(
                new NotificationEnvelope(eventKind, NotificationSeverity.Critical, "Host is unreachable", "Body"),
                null, default);
        }

        var page = await _admin.GetFromJsonAsync<NotificationDeliveryPageResponse>(
            $"/api/notification-deliveries?eventKind={eventKind}", Json);

        var delivery = Assert.Single(page!.Items);
        Assert.Equal("Host is unreachable", delivery.Subject);
        Assert.Equal(channel.Id, delivery.ChannelId);
        Assert.Equal("***@it-platform.local", delivery.TargetRedacted);
        // SMTP is disabled in this host, so the email channel logs instead of sending — which still
        // counts as delivered, and is exactly what a dev run does.
        Assert.Equal(NotificationDeliveryOutcome.Sent, delivery.Outcome);
    }

    [Fact]
    public async Task DigestRun_TriggeredByHand_IsAcceptedAndReportsNothingToDo()
    {
        var response = await _admin.PostAsync("/api/notification-digests/runs", null);

        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<NotificationDigestReport>(Json);
        Assert.Equal(0, report.Failed);
    }

    private async Task<NotificationChannelResponse> CreateChannelAsync(
        string name,
        NotificationChannelKind kind,
        string target)
    {
        var response = await _admin.PostAsJsonAsync("/api/notification-channels", new
        {
            name = $"{name}-{_suffix}",
            kind = kind.ToString(),
            target,
            description = (string?)null,
            isActive = true,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NotificationChannelResponse>(Json))!;
    }

    private async Task CreateRuleAsync(
        string name,
        Guid channelId,
        NotificationSeverity minimum,
        string? eventKind = null)
    {
        var response = await _admin.PostAsJsonAsync("/api/notification-routing-rules", new
        {
            name = $"{name}-{_suffix}",
            channelId,
            eventKind,
            minimumSeverity = minimum.ToString(),
            deviceGroup = (string?)null,
            quietHoursStart = (string?)null,
            quietHoursEnd = (string?)null,
            timeZone = "UTC",
            digestQuietHours = true,
            isActive = true,
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed record ValidationProblem(IDictionary<string, string[]> Errors);

    private sealed record ProblemDetailsBody(string? Title, string? Detail);

    private sealed class NotificationApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public NotificationApplication(
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
                    ["Notifications:DeepLinkBaseUrl"] = "https://app.example.test",
                }));
            builder.ConfigureServices(services =>
            {
                // No scheduler: the digest job would otherwise run against a database the whole
                // collection shares and release another test's held notifications.
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = NotificationAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = NotificationAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = NotificationAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, NotificationAuthenticationHandler>(
                        NotificationAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
        }
    }

    private sealed class NotificationAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "NotificationTest";
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
                    new Claim(ClaimTypes.NameIdentifier, $"notification-test-{role}"),
                    new Claim(ClaimTypes.Name, $"notification-test-{role}"),
                    new Claim(ClaimTypes.Role, role),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
