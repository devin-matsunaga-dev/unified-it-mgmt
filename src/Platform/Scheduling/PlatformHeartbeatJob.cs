using Microsoft.Extensions.Logging;

using Quartz;

namespace Platform.Scheduling;

[DisallowConcurrentExecution]
public sealed class PlatformHeartbeatJob(ILogger<PlatformHeartbeatJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        logger.LogInformation("Platform scheduler heartbeat at {HeartbeatTime}", DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}