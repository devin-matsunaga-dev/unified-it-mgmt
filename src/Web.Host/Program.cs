using Web.Host;

var builder = WebApplication.CreateBuilder(args);

builder.AddNpgsqlDataSource("database");
builder.AddRedisClient("redis");
builder.AddRabbitMQClient("rabbitmq");
builder.Services.AddHttpClient();
builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<DependencyEndpointHealthCheck>(
        "keycloak",
        args: ["keycloak", "/realms/master"])
    .AddTypeActivatedCheck<DependencyEndpointHealthCheck>(
        "minio",
        args: ["minio", "/minio/health/live"]);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "IT Platform" }));
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
