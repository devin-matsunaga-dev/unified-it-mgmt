using Quartz;

namespace Modules.Helpdesk.Features.Sla;

[DisallowConcurrentExecution]
public sealed class SlaEvaluationJob(ISlaService service) : IJob
{
    public Task Execute(IJobExecutionContext context) =>
        service.EvaluateAsync(DateTimeOffset.UtcNow, context.CancellationToken);
}
