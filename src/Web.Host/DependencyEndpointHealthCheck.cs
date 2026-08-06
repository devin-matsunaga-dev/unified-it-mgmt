using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Web.Host;

public sealed class DependencyEndpointHealthCheck(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    string connectionName,
    string healthPath) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString(connectionName);
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var baseUri))
        {
            return HealthCheckResult.Unhealthy(
                $"Connection string '{connectionName}' is missing or invalid.");
        }

        try
        {
            var endpoint = new Uri(baseUri, healthPath);
            using var response = await httpClientFactory.CreateClient().GetAsync(endpoint, cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    $"Dependency '{connectionName}' returned HTTP {(int)response.StatusCode}.");
        }
        catch (HttpRequestException exception)
        {
            return HealthCheckResult.Unhealthy(
                $"Dependency '{connectionName}' could not be reached.",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"Dependency '{connectionName}' timed out.",
                exception);
        }
    }
}
