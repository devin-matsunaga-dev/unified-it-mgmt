using Web.Host;
using Web.Host.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.AddNpgsqlDataSource("database");
builder.AddRedisClient("redis");
builder.AddRabbitMQClient("rabbitmq");
builder.Services.AddHttpClient();
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<DependencyEndpointHealthCheck>(
        "keycloak",
        args: ["keycloak", "/realms/master"])
    .AddTypeActivatedCheck<DependencyEndpointHealthCheck>(
        "minio",
        args: ["minio", "/minio/health/live"]);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "IT Platform" }));
app.MapHealthChecks("/health");
app.MapAuthenticationEndpoints();

app.Run();

public partial class Program;
