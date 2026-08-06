using Microsoft.EntityFrameworkCore;
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
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddPlatformServices(builder.Configuration);
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
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "IT Platform" }));
app.MapHealthChecks("/health");
app.MapAuthenticationEndpoints();
app.MapPlatformEndpoints();

app.Run();

public partial class Program;
