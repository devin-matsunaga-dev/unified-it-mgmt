using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Web.Host;

namespace Infrastructure.Tests;

public sealed class DependencyEndpointHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_MissingConnectionString_ReturnsUnhealthy()
    {
        var configuration = new ConfigurationBuilder().Build();
        var healthCheck = new DependencyEndpointHealthCheck(
            configuration,
            new StubHttpClientFactory(),
            "missing",
            "/health");

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("missing or invalid", result.Description, StringComparison.Ordinal);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
