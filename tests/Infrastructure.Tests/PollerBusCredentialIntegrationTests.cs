using System.Text;

using Platform.Messaging;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

using Testcontainers.RabbitMq;

namespace Infrastructure.Tests;

/// <summary>
/// A broker of this class's own, deliberately not the shared one.
/// <para>
/// Importing a definitions document is a broker-wide act: it rewrites accounts and re-declares
/// topology, and doing that underneath the running MassTransit bus in
/// <see cref="PollerHeartbeatBusIntegrationTests"/> left that bus connected to a broker whose
/// topology had moved, so its heartbeats silently never arrived. Isolation is cheaper than
/// reasoning about the blast radius of an import.
/// </para>
/// </summary>
public sealed class PollerBusFixture : IAsyncLifetime
{
    public const string AdminUsername = "itplatform";
    public const string AdminPassword = "poller-bus-fixture-password";
    public const string PollerUsername = "poller-credential-test";
    public const string PollerPassword = "poller-credential-test-password";

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername(AdminUsername)
        .WithPassword(AdminPassword)
        .Build();

    public string ConnectionString => _rabbitMq.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _rabbitMq.StartAsync();

        // The document that ships, rendered by the code AppHost renders it with. A permission model
        // asserted against a hand-written copy proves nothing about the one that runs.
        var definitions = RabbitMqDefinitions.Render(
        [
            RabbitMqDefinitions.Administrator(AdminUsername, AdminPassword),
            RabbitMqDefinitions.PublishOnlyPoller(PollerUsername, PollerPassword),
        ]);
        await _rabbitMq.CopyAsync(Encoding.UTF8.GetBytes(definitions), "/tmp/definitions.json");
        var result = await _rabbitMq.ExecAsync(["rabbitmqctl", "import_definitions", "/tmp/definitions.json"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Importing the RabbitMQ definitions failed ({result.ExitCode}): {result.Stderr}{result.Stdout}");
        }
    }

    public Task DisposeAsync() => _rabbitMq.DisposeAsync().AsTask();
}

/// <summary>
/// The WP's security claim, tested against a real broker: "poller creds cannot consume other queues
/// (attempt fails)".
/// </summary>
public sealed class PollerBusCredentialIntegrationTests(PollerBusFixture broker)
    : IClassFixture<PollerBusFixture>, IAsyncLifetime
{
    private const string ForeignExchange = "Contracts.Events:TicketCreated";
    private const string ForeignQueue = "poller-credential-test-queue";

    public async Task InitializeAsync()
    {
        // Something for the poller to fail to read: an exchange and a queue it has no business with,
        // standing in for the ticket and alert traffic that shares a broker with it in production.
        await using var connection = await OpenAsync(PollerBusFixture.AdminUsername, PollerBusFixture.AdminPassword);
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(ForeignExchange, ExchangeType.Fanout, durable: true);
        await channel.QueueDeclareAsync(ForeignQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(ForeignQueue, ForeignExchange, routingKey: string.Empty);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The credential works at all — otherwise every refusal below proves nothing. It also proves
    /// the password hash the renderer writes is one RabbitMQ accepts.
    /// </summary>
    [Fact]
    public async Task PollerCredential_PublishingToTheHeartbeatExchange_Succeeds()
    {
        await using var connection = await OpenAsync(
            PollerBusFixture.PollerUsername, PollerBusFixture.PollerPassword);
        await using var channel = await CreateConfirmingChannelAsync(connection);

        await channel.BasicPublishAsync(
            RabbitMqDefinitions.PollerHeartbeatExchange,
            routingKey: string.Empty,
            mandatory: false,
            body: Encoding.UTF8.GetBytes("{}"));

        Assert.True(channel.IsOpen);
    }

    [Fact]
    public async Task PollerCredential_ConsumingAnotherQueue_IsRefused()
    {
        await using var connection = await OpenAsync(
            PollerBusFixture.PollerUsername, PollerBusFixture.PollerPassword);
        await using var channel = await connection.CreateChannelAsync();

        var refusal = await Assert.ThrowsAnyAsync<OperationInterruptedException>(() =>
            channel.BasicConsumeAsync(ForeignQueue, autoAck: true, new AsyncEventingBasicConsumer(channel)));

        AssertAccessRefused(refusal);
    }

    [Fact]
    public async Task PollerCredential_DeclaringAQueue_IsRefused()
    {
        await using var connection = await OpenAsync(
            PollerBusFixture.PollerUsername, PollerBusFixture.PollerPassword);
        await using var channel = await connection.CreateChannelAsync();

        var refusal = await Assert.ThrowsAnyAsync<OperationInterruptedException>(() =>
            channel.QueueDeclareAsync(
                $"poller-attempt-{Guid.NewGuid():N}", durable: false, exclusive: false, autoDelete: true));

        AssertAccessRefused(refusal);
    }

    /// <summary>
    /// Write permission is one exchange, not "any exchange": a poller that could publish anywhere
    /// could forge a TicketCreated.
    /// </summary>
    [Fact]
    public async Task PollerCredential_PublishingToAnotherExchange_IsRefused()
    {
        await using var connection = await OpenAsync(
            PollerBusFixture.PollerUsername, PollerBusFixture.PollerPassword);
        await using var channel = await CreateConfirmingChannelAsync(connection);

        var refusal = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await channel.BasicPublishAsync(
                ForeignExchange,
                routingKey: string.Empty,
                mandatory: false,
                body: Encoding.UTF8.GetBytes("{}")));

        Assert.Contains("access", refusal.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollerCredential_DeclaringAnExchange_IsRefused()
    {
        await using var connection = await OpenAsync(
            PollerBusFixture.PollerUsername, PollerBusFixture.PollerPassword);
        await using var channel = await connection.CreateChannelAsync();

        var refusal = await Assert.ThrowsAnyAsync<OperationInterruptedException>(() =>
            channel.ExchangeDeclareAsync(
                $"poller-attempt-{Guid.NewGuid():N}", ExchangeType.Fanout, durable: false));

        AssertAccessRefused(refusal);
    }

    private static void AssertAccessRefused(Exception refusal) =>
        Assert.Contains("ACCESS_REFUSED", refusal.ToString(), StringComparison.Ordinal);

    private async Task<IConnection> OpenAsync(string username, string password)
    {
        var uri = new Uri(broker.ConnectionString);
        var factory = new ConnectionFactory
        {
            HostName = uri.Host,
            Port = uri.Port,
            UserName = username,
            Password = password,
        };
        return await factory.CreateConnectionAsync();
    }

    /// <summary>
    /// Confirms on, so a refusal surfaces at the publish rather than on whatever operation happens
    /// to follow it — an unconfirmed publish to a forbidden exchange returns before the broker has
    /// said no.
    /// </summary>
    private static Task<IChannel> CreateConfirmingChannelAsync(IConnection connection) =>
        connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true));
}
