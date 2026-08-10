using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Modules.Assets;
using Modules.Assets.Data;
using Modules.Assets.Features.BulkEdit;
using Modules.Assets.Features.Cis;
using Modules.Assets.Features.Contracts;
using Modules.Assets.Features.Import;
using Modules.Assets.Features.Labels;
using Modules.Assets.Features.Lifecycle;
using Modules.Assets.Features.Relationships;
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
using Modules.Monitoring;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Modules.Monitoring.Features.MaintenanceWindows;
using Modules.Monitoring.Features.Metrics;
using Modules.Monitoring.Features.PollerConfig;
using Platform;
using Platform.Auditing;
using Platform.Data;
using Web.Host;
using Web.Host.Authentication;
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
    .AllowAnyMethod()));
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddPlatformServices(
    builder.Configuration,
    MonitoringServiceCollectionExtensions.AddMonitoringConsumers);
builder.Services.AddHelpdeskServices(builder.Configuration);
builder.Services.AddAssetsServices(builder.Configuration);
builder.Services.AddMonitoringServices(builder.Configuration);
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
app.MapMonitoredDeviceEndpoints();
app.MapMaintenanceWindowEndpoints();
app.MapMetricEndpoints();
app.MapPollerEndpoints();

app.Run();

public partial class Program;
