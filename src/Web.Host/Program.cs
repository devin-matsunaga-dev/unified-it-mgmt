using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Modules.Assets;
using Modules.Assets.Data;
using Modules.Assets.Features.BulkEdit;
using Modules.Assets.Features.Cis;
using Modules.Assets.Features.Contracts;
using Modules.Assets.Features.Discovery;
using Modules.Assets.Features.Drift;
using Modules.Assets.Features.Impact;
using Modules.Assets.Features.Import;
using Modules.Assets.Features.Labels;
using Modules.Assets.Features.Lifecycle;
using Modules.Assets.Features.PhysicalAudits;
using Modules.Assets.Features.Relationships;
using Modules.Assets.Features.Software;
using Modules.Assets.Features.Timeline;
using Modules.Assets.Features.Topology;
using Modules.Helpdesk;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.TicketCis;
using Modules.Helpdesk.Features.Tickets;
using Modules.Helpdesk.Features.Assignments;
using Modules.Helpdesk.Features.CannedResponses;
using Modules.Helpdesk.Features.Categories;
using Modules.Helpdesk.Features.Views;
using Modules.Helpdesk.Features.Interactions;
using Modules.Helpdesk.Features.Sla;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Monitoring;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;
using Modules.Monitoring.Features.Dashboards;
using Modules.Monitoring.Features.Devices;
using Modules.Monitoring.Features.Discovery;
using Modules.Monitoring.Features.Interfaces;
using Modules.Monitoring.Features.MaintenanceWindows;
using Modules.Monitoring.Features.Metrics;
using Modules.Monitoring.Features.PollerConfig;
using Platform;
using Platform.Auditing;
using Platform.Data;
using Platform.Notifications;
using Platform.Vault;
using Web.Host;
using Web.Host.Authentication;
using Web.Host.Hubs;
using Web.Host.Platform;

var builder = WebApplication.CreateBuilder(args);

builder.AddNpgsqlDataSource("database");
builder.AddRedisClient("redis");
builder.AddRabbitMQClient("rabbitmq");
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(options => options.AddPolicy("WebClient", policy => policy
    .WithOrigins(builder.Configuration["WebClient:Origin"] ?? "http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    // A SignalR connection carries credentials, and the browser refuses a credentialed cross-origin
    // request against a wildcard origin. The origin here is already a single configured value.
    .AllowCredentials()));
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddPlatformServices(
    builder.Configuration,
    bus =>
    {
        MonitoringServiceCollectionExtensions.AddMonitoringConsumers(bus);
        HelpdeskServiceCollectionExtensions.AddHelpdeskConsumers(bus);
        AssetsServiceCollectionExtensions.AddAssetsConsumers(bus);
    });
builder.Services.AddHelpdeskServices(builder.Configuration);
builder.Services.AddAssetsServices(builder.Configuration);
builder.Services.AddMonitoringServices(builder.Configuration);

// WP-3.9. The hub is the host's (ARCHITECTURE §2) and the Redis backplane is what makes two API
// instances agree about who is connected — without it a board would only see the alerts that
// happened to be evaluated by the instance its socket landed on. Gated on the connection string so a
// test host, which has no Redis, still starts; every real deployment has one.
var signalR = builder.Services.AddSignalR();
if (builder.Configuration.GetConnectionString("redis") is { Length: > 0 } redisConnectionString)
{
    signalR.AddStackExchangeRedis(redisConnectionString,
        options => options.Configuration.ChannelPrefix =
            StackExchange.Redis.RedisChannel.Literal("it-platform:monitoring"));
}

builder.Services.Replace(
    ServiceDescriptor.Scoped<IMonitoringBroadcaster, SignalRMonitoringBroadcaster>());

builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<DependencyEndpointHealthCheck>(
        "keycloak",
        args: ["keycloak", "/realms/master"])
    .AddTypeActivatedCheck<DependencyEndpointHealthCheck>(
        "minio",
        args: ["minio", "/minio/health/live"]);

var app = builder.Build();

if (app.Configuration.GetValue("Platform:ApplyMigrations", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors("WebClient");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "IT Platform" }));
app.MapHealthChecks("/health");
app.MapAuthenticationEndpoints();
app.MapPlatformEndpoints();
app.MapDirectoryEndpoints();
app.MapNotificationEndpoints();
app.MapCredentialEndpoints();
app.MapSystemPingEndpoints();
app.MapTicketEndpoints();
app.MapTicketCiLinkEndpoints();
app.MapAssignmentEndpoints();
app.MapInteractionEndpoints();
app.MapSlaEndpoints();
app.MapCategoryEndpoints();
app.MapTicketViewEndpoints();
app.MapCannedResponseEndpoints();
app.MapCiEndpoints();
app.MapCiLifecycleEndpoints();
app.MapCiRelationshipEndpoints();
app.MapCiImportEndpoints();
app.MapCiBulkEditEndpoints();
app.MapContractEndpoints();
app.MapCiLabelEndpoints();
app.MapDiscoveryReviewEndpoints();
app.MapTopologyEndpoints();
app.MapDriftEndpoints();
app.MapImpactEndpoints();
app.MapCiTimelineEndpoints();
app.MapPhysicalAuditEndpoints();
app.MapSoftwareEndpoints();
app.MapMonitoredDeviceEndpoints();
app.MapMaintenanceWindowEndpoints();
app.MapMetricEndpoints();
app.MapInterfaceEndpoints();
app.MapPollerEndpoints();
app.MapScanProfileEndpoints();
app.MapAlertEndpoints();
app.MapDashboardEndpoints();
app.MapHub<MonitoringHub>("/hubs/monitoring");

app.Run();

public partial class Program;
