using Contracts.Events;

using MassTransit;

namespace Modules.Monitoring.Features.Heartbeats;

/// <summary>
/// The platform's half of the heartbeat: it records that a poller is alive. It is the only consumer
/// of anything a poller publishes, and the poller's bus credential can reach nothing else.
/// </summary>
public sealed class PollerHeartbeatConsumer(IPollerHeartbeatService service) : IConsumer<PollerHeartbeat>
{
    public Task Consume(ConsumeContext<PollerHeartbeat> context) =>
        service.RecordAsync(context.Message, context.CancellationToken);
}
