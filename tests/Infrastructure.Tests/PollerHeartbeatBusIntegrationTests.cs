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
/// The wiring the API tests cannot see: a heartbeat published onto the real broker reaches the
/// consumer that lives in Monitoring, through Platform's single MassTransit registration.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PollerHeartbeatBusIntegrationTests(InfrastructureFixture infrastructure) : IAsyncLifetime
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
    public async Task PollerHeartbeat_PublishedOnTheBus_ReachesTheMonitoringConsumer()
    {
        var name = $"bus-{Guid.NewGuid():N}"[..20];
        await RegisterAsync(name);

        await PublishAsync(new PollerHeartbeat(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            name,
            "default",
            "0.1.0",
            ConfigVersion: 12,
            IntervalSeconds: 15,
            DeviceCount: 4,
            CycleNumber: 3));

        var poller = await WaitForAsync(name, poller => poller.LastHeartbeatAt is not null);

        Assert.Equal(3, poller.LastCycleNumber);
        Assert.Equal(4, poller.LastReportedDeviceCount);
        Assert.Equal(15, poller.HeartbeatIntervalSeconds);
    }

    /// <summary>
    /// The failure path on this side of the bus: a heartbeat naming a poller nobody registered is
    /// dropped rather than creating one, and the consumer must not fault the message on it.
    /// </summary>
    [Fact]
    public async Task PollerHeartbeat_ForAnUnregisteredPoller_IsConsumedWithoutFaulting()
    {
        var unknown = $"ghost-{Guid.NewGuid():N}"[..20];
        var known = $"bus-{Guid.NewGuid():N}"[..20];
        await RegisterAsync(known);

        await PublishAsync(Beat(unknown));
        await PublishAsync(Beat(known));

        // The second message is the probe: it can only arrive if the first was consumed rather than
        // left retrying or dead-lettered.
        await WaitForAsync(known, poller => poller.LastHeartbeatAt is not null);

        await using var scope = _services!.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        Assert.Null(await context.Pollers.SingleOrDefaultAsync(item => item.Name == unknown));
    }

    private static PollerHeartbeat Beat(string name) => new(
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        name,
        "default",
        "0.1.0",
        ConfigVersion: 0,
        IntervalSeconds: 15,
        DeviceCount: 0,
        CycleNumber: 1);

    private async Task RegisterAsync(string name)
    {
        await using var scope = _services!.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        context.Pollers.Add(new Poller
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            PollerGroup = "default",
            RegisteredAt = DateTimeOffset.UtcNow,
            LastRegisteredAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task PublishAsync(PollerHeartbeat heartbeat)
    {
        await using var scope = _services!.CreateAsyncScope();
        // Through the outbox, like every other publish in the solution: the message is written with
        // the transaction and delivered afterwards.
        await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(heartbeat);
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
    }

    private async Task<Poller> WaitForAsync(string name, Func<Poller, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = _services!.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            var poller = await context.Pollers.AsNoTracking().SingleOrDefaultAsync(item => item.Name == name);
            if (poller is not null && condition(poller))
            {
                return poller;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"The heartbeat for poller '{name}' never arrived. Queues:{Environment.NewLine}" +
            await infrastructure.DescribeRabbitMqQueuesAsync());
    }
}
