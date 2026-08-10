
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
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
        .WithUsername(RabbitMqUsername)
        .WithPassword(RabbitMqPassword)
        .Build();
    private readonly IContainer _minio = new ContainerBuilder("quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z")
        .WithCommand("server", "/data")
        .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
        .WithEnvironment("MINIO_ROOT_PASSWORD", "minio-test-password")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
            request.ForPort(9000).ForPath("/minio/health/live")))
        .Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public const string RabbitMqUsername = "itplatform";
    public const string RabbitMqPassword = "itplatform-test-password";

    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    /// <summary>
    /// Queue names and depths, for a test that timed out waiting for a message. "It never arrived"
    /// and "it arrived and the consumer faulted" are different bugs and the error queue tells them
    /// apart.
    /// </summary>
    public async Task<string> DescribeRabbitMqQueuesAsync()
    {
        var result = await _rabbitMq.ExecAsync(["rabbitmqctl", "list_queues", "name", "messages"]);
        return result.ExitCode == 0 ? result.Stdout : $"(could not list queues: {result.Stderr})";
    }

    // Deliberately no definitions-import helper here. Importing definitions is a broker-wide act and
    // this broker is shared by every test in the collection, including one with a running MassTransit
    // bus; PollerBusCredentialIntegrationTests starts a broker of its own instead.
    public string MinioConnectionString => $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";

    public Task InitializeAsync() => Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync(), _minio.StartAsync());

    public Task DisposeAsync() => Task.WhenAll(
        _postgres.DisposeAsync().AsTask(),
        _rabbitMq.DisposeAsync().AsTask(),
        _minio.DisposeAsync().AsTask());
}

[CollectionDefinition(Name)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "Postgres and RabbitMQ infrastructure";
}
