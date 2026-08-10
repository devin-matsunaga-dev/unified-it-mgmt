using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Modules.Monitoring;
using Modules.Monitoring.Data;

using Platform;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The wiring the storage tests cannot see. Until this package the telemetry exchange was a fanout
/// with nothing bound to it and every message a poller published was discarded by the broker; the
/// whole of "ingestion turns on" is that <c>DeviceTelemetryConsumer</c> is now registered, and this
/// is what proves it — no poller change, a real publish, rows in the hypertable.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PollerTelemetryBusIntegrationTests(InfrastructureFixture infrastructure) : IAsyncLifetime
{
    private ServiceProvider? _services;
    private List<IHostedService> _hostedServices = [];

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:database"] = infrastructure.PostgresConnectionString,
                ["ConnectionStrings:rabbitmq"] = infrastructure.RabbitMqConnectionString,
                ["Platform:EnableScheduler"] = "false",
            })
            .Build();
        _services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddPlatformServices(configuration, MonitoringServiceCollectionExtensions.AddMonitoringConsumers)
            .AddMonitoringServices(configuration)
            .BuildServiceProvider(validateScopes: true);

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
        }

        _hostedServices = [.. _services.GetServices<IHostedService>()];
        foreach (var hostedService in _hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    public async Task DisposeAsync()
    {
        for (var index = _hostedServices.Count - 1; index >= 0; index--)
        {
            await _hostedServices[index].StopAsync(CancellationToken.None);
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    [Fact]
    public async Task Telemetry_PublishedOnTheBus_ReachesTheHypertable()
    {
        var deviceId = Guid.CreateVersion7();
        var checkId = Guid.CreateVersion7();
        var metric = $"bus.metric.{Guid.NewGuid():N}";
        var observed = DateTimeOffset.UtcNow;

        await PublishAsync(Batch(deviceId, checkId, metric, 55d, observed));

        var stored = await WaitForAsync(metric, rows => rows.Count > 0);

        Assert.Equal(55d, stored.Single(row => row.MetricName == metric).Value);
        Assert.Equal(deviceId, stored[0].DeviceId);
    }

    /// <summary>
    /// The Platform dedupe helper, which WP-3.2's heartbeat consumer deliberately does not use. Two
    /// deliveries of one telemetry event are two different broker messages carrying the same
    /// <c>EventId</c>, and only the first is ingested.
    /// </summary>
    [Fact]
    public async Task Telemetry_DeliveredTwice_IsIngestedOnce()
    {
        var deviceId = Guid.CreateVersion7();
        var checkId = Guid.CreateVersion7();
        var metric = $"bus.metric.{Guid.NewGuid():N}";
        var observed = DateTimeOffset.UtcNow;
        var telemetry = Batch(deviceId, checkId, metric, 12d, observed);

        await PublishAsync(telemetry);
        await WaitForAsync(metric, rows => rows.Count > 0);

        // Same event, second delivery — and a different metric behind it, so there is something to
        // wait for that proves the second message was consumed rather than merely slow.
        var probe = $"bus.probe.{Guid.NewGuid():N}";
        await PublishAsync(telemetry);
        await PublishAsync(Batch(deviceId, checkId, probe, 1d, observed.AddSeconds(1)));
        await WaitForAsync(probe, rows => rows.Count > 0);

        await using var scope = _services!.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        Assert.Equal(1, await context.DeviceMetrics.CountAsync(row => row.MetricName == metric));

        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.True(await platform.ConsumerDedupeEntries
            .AnyAsync(entry => entry.Key == $"device-telemetry:{telemetry.EventId}"));
    }

    private static DeviceTelemetryReported Batch(
        Guid deviceId,
        Guid checkId,
        string metric,
        double value,
        DateTimeOffset observedAt) => new(
        Guid.CreateVersion7(),
        observedAt,
        "bus-poller",
        "default",
        CycleNumber: 1,
        [
            new DeviceCheckResult(
                deviceId, Guid.CreateVersion7(), checkId, "Snmp", "CPU", "10.40.0.1", observedAt,
                Succeeded: true, LatencyMs: 2d, Error: null,
                Metrics: [new MetricSample(metric, value, null, "%")]),
        ]);

    private async Task PublishAsync(DeviceTelemetryReported telemetry)
    {
        await using var scope = _services!.CreateAsyncScope();
        // Through the outbox, like every other publish in the solution.
        await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(telemetry);
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
    }

    private async Task<List<DeviceMetric>> WaitForAsync(string metric, Func<List<DeviceMetric>, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = _services!.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            var rows = await context.DeviceMetrics.AsNoTracking()
                .Where(row => row.MetricName == metric)
                .ToListAsync();
            if (condition(rows))
            {
                return rows;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Telemetry carrying '{metric}' never arrived. Queues:{Environment.NewLine}" +
            await infrastructure.DescribeRabbitMqQueuesAsync());
    }
}
