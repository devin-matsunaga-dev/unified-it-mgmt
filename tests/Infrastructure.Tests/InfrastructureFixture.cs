using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Infrastructure.Tests;

public sealed class InfrastructureFixture : IAsyncLifetime
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

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    public Task InitializeAsync() => Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

    public Task DisposeAsync() => Task.WhenAll(
        _postgres.DisposeAsync().AsTask(),
        _rabbitMq.DisposeAsync().AsTask());
}

[CollectionDefinition(Name)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "Postgres and RabbitMQ infrastructure";
}
