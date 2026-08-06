using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Infrastructure.Tests;

public sealed class HealthEndpointTests : IAsyncLifetime
{
    private static readonly string[] ConnectionNames = ["database", "redis", "rabbitmq", "keycloak", "minio"];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("it_platform")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private WebApplication? _httpDependency;
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _postgres.StartAsync(),
            _redis.StartAsync(),
            _rabbitMq.StartAsync());

        var dependencyBuilder = WebApplication.CreateBuilder();
        dependencyBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        _httpDependency = dependencyBuilder.Build();
        _httpDependency.MapGet("/realms/master", () => Results.Ok());
        _httpDependency.MapGet("/minio/health/live", () => Results.Ok());
        await _httpDependency.StartAsync();

        var address = _httpDependency.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:database"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:redis"] = _redis.GetConnectionString(),
            ["ConnectionStrings:rabbitmq"] = _rabbitMq.GetConnectionString(),
            ["ConnectionStrings:keycloak"] = address,
            ["ConnectionStrings:minio"] = address,
        };

        foreach (var setting in settings)
        {
            Environment.SetEnvironmentVariable(setting.Key.Replace(':', '_').Replace("_", "__"), setting.Value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(webHost =>
                webHost.ConfigureAppConfiguration(configuration =>
                    configuration.AddInMemoryCollection(settings)));
    }

    [Fact]
    public async Task Health_AllDependenciesReachable_ReturnsHealthy()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    public async Task DisposeAsync()
    {
        foreach (var connectionName in ConnectionNames)
        {
            Environment.SetEnvironmentVariable($"ConnectionStrings__{connectionName}", null);
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_httpDependency is not null)
        {
            await _httpDependency.DisposeAsync();
        }

        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask(),
            _rabbitMq.DisposeAsync().AsTask());
    }
}
