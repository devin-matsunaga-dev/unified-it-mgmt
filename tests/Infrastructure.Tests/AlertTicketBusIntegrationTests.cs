using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Modules.Assets;
using Modules.Assets.Data;
using Modules.Helpdesk;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.AlertTickets;
using Modules.Monitoring;

using Platform;
using Platform.Data;

using StackExchange.Redis;

namespace Infrastructure.Tests;

/// <summary>
/// The wiring the automation tests cannot see. Everything else in this package proves what happens
/// once an alert reaches the automation; this proves an alert reaches it at all — that
/// <c>AlertRaised</c> and <c>AlertCleared</c> now have a queue bound to their exchanges, which until
/// this package they did not.
/// <para>
/// The same failure mode WP-3.4's notes describe for telemetry: an event nothing binds is discarded
/// by the broker, and every test that drives the consumer directly still passes.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class AlertTicketBusIntegrationTests(InfrastructureFixture infrastructure) : IAsyncLifetime
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
                ["ConnectionStrings:redis"] = infrastructure.RedisConnectionString,
                ["ConnectionStrings:minio"] = infrastructure.MinioConnectionString,
                ["ObjectStorage:AccessKey"] = "minioadmin",
                ["ObjectStorage:SecretKey"] = "minio-test-password",
                ["Platform:EnableScheduler"] = "false",
                // High enough that this class's own messages never trip the breaker, whatever else
                // has been through the shared Redis before it.
                [$"{AlertTicketOptions.SectionName}:BreakerThreshold"] = "1000",
            })
            .Build();
        _services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(infrastructure.RedisConnectionString))
            .AddPlatformServices(configuration, bus =>
            {
                MonitoringServiceCollectionExtensions.AddMonitoringConsumers(bus);
                HelpdeskServiceCollectionExtensions.AddHelpdeskConsumers(bus);
            })
            .AddHelpdeskServices(configuration)
            .AddMonitoringServices(configuration)
            // Assets too, although nothing here is about assets: WP-3.7's automation reads the CMDB
            // through `ICiDirectory`, which only Assets registers, and a consumer that cannot be
            // constructed faults its message into the error queue with nothing to explain it.
            .AddAssetsServices(configuration)
            // After AddPlatformServices, never before: options configure actions run in registration
            // order, so anything set first is simply overwritten by MassTransit's own defaults.
            //
            // WaitUntilStarted, so the first publish cannot race the bus into declaring its queues and
            // bindings — a message published to a fanout with nothing bound is discarded, leaving an
            // empty queue, no error queue and nothing to explain it (the WP-3.2 topology trap).
            .Configure<MassTransitHostOptions>(options =>
            {
                options.WaitUntilStarted = true;
                options.StartTimeout = TimeSpan.FromSeconds(60);
            })
            // And a fast, wide outbox sweep, because the transactional outbox is a shared Platform
            // table: every test host that runs on the in-memory bus removes its hosted services, so
            // its published rows are never delivered and simply accumulate. By the time this class
            // runs there are dozens of them ahead of ours and the default sweep is far too slow to
            // reach ours. This is what made the test pass alone and fail in the full suite — the
            // message was never lost, only queued behind other tests' leftovers.
            .Configure<OutboxDeliveryServiceOptions>(options =>
            {
                options.QueryDelay = TimeSpan.FromMilliseconds(250);
                options.QueryMessageLimit = 500;
            })
            .BuildServiceProvider(validateScopes: true);

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
        }

        await _services.GetRequiredService<IConnectionMultiplexer>().GetDatabase()
            .KeyDeleteAsync([RedisAlertAutomationGuard.BreakerKey, RedisAlertAutomationGuard.WindowKey]);

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

    /// <summary>
    /// Raise on the bus, clear on the bus, and read the ticket the automation left behind. One test
    /// for both consumers on purpose: the clear can only be proved against a ticket the raise opened.
    /// </summary>
    [Fact]
    public async Task AlertRaisedThenCleared_PublishedOnTheBus_OpensATicketAndResolvesIt()
    {
        var deviceId = Guid.CreateVersion7();
        var checkId = Guid.CreateVersion7();
        var ruleId = $"check:{checkId}:cpu.utilisation_percent";
        var key = AlertTicketPolicy.DedupeKey(deviceId, ruleId);
        var alertId = Guid.CreateVersion7();

        await PublishAsync(new AlertRaised(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, alertId, deviceId, Guid.CreateVersion7(), checkId,
            ruleId, "SNMP: CPU", "Critical", "cpu.utilisation_percent", 98d, 90d,
            "CPU utilisation is above the critical threshold.", DateTimeOffset.UtcNow, 3));

        var opened = await WaitForAsync(key, entry => entry?.TicketId is not null);
        Assert.Equal(1, opened!.OccurrenceCount);

        await PublishAsync(new AlertCleared(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, alertId, deviceId, Guid.CreateVersion7(), checkId,
            ruleId, "SNMP: CPU", "Critical", "cpu.utilisation_percent", 8d,
            "CPU utilisation is back below the threshold.", DateTimeOffset.UtcNow.AddMinutes(-4), 240));

        var resolved = await WaitForAsync(key, entry => entry?.AutoResolvedAt is not null);

        await using var scope = _services!.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var ticket = await context.Tickets.Include(item => item.Status)
            .SingleAsync(item => item.Id == resolved!.TicketId);
        Assert.Equal("Resolved", ticket.Status.Name);
    }

    private async Task PublishAsync(object message)
    {
        await using var scope = _services!.CreateAsyncScope();
        // Through the outbox, like every other publish in the solution.
        await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(message);
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
    }

    private async Task<AlertTicket?> WaitForAsync(string dedupeKey, Func<AlertTicket?, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        AlertTicket? entry = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = _services!.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            entry = await context.AlertTickets.AsNoTracking()
                .SingleOrDefaultAsync(item => item.DedupeKey == dedupeKey);
            if (condition(entry))
            {
                return entry;
            }

            await Task.Delay(200);
        }

        // Three different failures look identical from a bare timeout, and this tells them apart:
        // an undelivered outbox backlog means the message never left this host; an empty queue with a
        // written row means it arrived and the automation declined to ticket it; neither means the
        // binding is missing. All three have happened while writing this package.
        string diagnostic;
        await using (var probe = _services!.CreateAsyncScope())
        {
            var platform = probe.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var undelivered = await platform.Database
                .SqlQuery<int>($"select count(*)::int as \"Value\" from platform.outbox_states where delivered is null")
                .SingleAsync();
            diagnostic = $"undelivered outbox states: {undelivered}; ";
        }

        diagnostic += entry is null
            ? "no alert-ticket row was written"
            : $"row: tickets={entry.TicketCount}, suppressed={entry.SuppressedCount}, "
              + $"occurrences={entry.OccurrenceCount}, ticketId={entry.TicketId}, resolved={entry.AutoResolvedAt}";
        throw new TimeoutException(
            $"Nothing satisfied the condition for '{dedupeKey}' ({diagnostic}). Queues:{Environment.NewLine}" +
            await infrastructure.DescribeRabbitMqQueuesAsync());
    }
}
