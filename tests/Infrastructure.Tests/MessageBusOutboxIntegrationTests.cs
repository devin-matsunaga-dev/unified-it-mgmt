using System.Security.Claims;

using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Platform;
using Platform.Data;
using Platform.Messaging;

using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Infrastructure.Tests;

public sealed class MessageBusOutboxIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("it_platform")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername("itplatform")
        .WithPassword("itplatform-test-password")
        .Build();
    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:database"] = _postgres.GetConnectionString(),
                ["ConnectionStrings:rabbitmq"] = _rabbitMq.GetConnectionString(),
                ["Platform:EnableScheduler"] = "false",
            })
            .Build();
        _services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddPlatformServices(configuration)
            .BuildServiceProvider(validateScopes: true);

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
    }

    [Fact]
    public async Task Outbox_BusStartsAfterPublish_DeliversDurablyAndDeduplicatesConsumer()
    {
        const string dedupeKey = "integration-durable-ping";
        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "integration-admin")],
            "Test"));

        await PublishAsync(dedupeKey, actor);
        await PublishAsync(dedupeKey, actor);

        await using (var beforeBusScope = _services!.CreateAsyncScope())
        {
            var dbContext = beforeBusScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Equal(2, await dbContext.Set<OutboxMessage>().CountAsync());
            Assert.Empty(await dbContext.ConsumerDedupeEntries.ToListAsync());
        }

        var services = _services!;
        var hostedServices = services.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        try
        {
            await WaitForAsync(async () =>
            {
                await using var scope = services.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                return await dbContext.ConsumerDedupeEntries.CountAsync(
                    entry => entry.Key == $"system-ping:{dedupeKey}") == 1;
            });

            await using var verificationScope = services.CreateAsyncScope();
            var verificationContext = verificationScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var receiptAudits = await verificationContext.AuditEntries
                .Where(entry => entry.Action == "Received" && entry.EntityType == "SystemPing")
                .ToListAsync();
            Assert.Single(receiptAudits, entry =>
                entry.AfterJson?.Contains(dedupeKey, StringComparison.Ordinal) == true);
        }
        finally
        {
            for (var index = hostedServices.Count - 1; index >= 0; index--)
            {
                await hostedServices[index].StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task ExecuteOnceAsync_EmptyDedupeKey_RejectsInput()
    {
        await using var scope = _services!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IConsumerIdempotencyService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteOnceAsync("", _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Migrations_CurrentModel_HasNoPendingChanges()
    {
        await using var scope = _services!.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    private async Task PublishAsync(string dedupeKey, ClaimsPrincipal actor)
    {
        await using var scope = _services!.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<ISystemPingPublisher>();
        await publisher.PublishAsync(dedupeKey, actor);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }
}
