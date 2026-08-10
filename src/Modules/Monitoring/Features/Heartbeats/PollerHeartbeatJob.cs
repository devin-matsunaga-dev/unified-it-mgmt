using Quartz;

namespace Modules.Monitoring.Features.Heartbeats;

/// <summary>
/// Looks for pollers that have stopped speaking. Nothing else can notice: a heartbeat is the poller
/// telling the platform it is alive, so its absence has to be found by looking.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PollerHeartbeatJob(IPollerHeartbeatService service) : IJob
{
    public Task Execute(IJobExecutionContext context) => service.EvaluateAsync(context.CancellationToken);
}
