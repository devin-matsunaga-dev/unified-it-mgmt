using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Monitoring.Data;

using Quartz;

namespace Modules.Monitoring.Features.Runbooks;

public interface IRunbookTimeoutSweeper
{
    /// <summary>Finishes every dispatched execution whose deadline has passed. Returns how many.</summary>
    Task<int> SweepAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The timeout the WP asks for, enforced on the platform's side.
/// <para>
/// The agent enforces one too, and both are needed. The agent's stops a runbook running forever on a
/// host; this one stops an execution waiting forever for an agent that has been killed, redeployed or
/// partitioned — nothing else can notice that, because the only evidence is a result that never comes.
/// It is the same argument WP-3.2 makes for looking for missed heartbeats.
/// </para>
/// <para>
/// A timed-out execution is never re-dispatched. That is the "no retry storm" rule in its strictest
/// form: the platform does not know whether the runbook ran, and running a remediation a second time
/// when the first may have half-happened is worse than telling a human.
/// </para>
/// </summary>
public sealed class RunbookTimeoutSweeper(
    MonitoringDbContext dbContext,
    IRunbookCompletionService completionService,
    ILogger<RunbookTimeoutSweeper> logger) : IRunbookTimeoutSweeper
{
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var overdue = await dbContext.RunbookExecutions
            .Where(execution => execution.Status == RunbookExecutionStatus.Dispatched
                && execution.DeadlineAt != null
                && execution.DeadlineAt < now)
            .Select(execution => new { Execution = execution, execution.Runbook.Name })
            .ToListAsync(cancellationToken);
        if (overdue.Count == 0)
        {
            return 0;
        }

        foreach (var row in overdue)
        {
            logger.LogWarning(
                "Runbook '{RunbookKey}' (execution {ExecutionId}) passed its deadline of {DeadlineAt:u} with no result from {PollerName}.",
                row.Execution.RunbookKey, row.Execution.Id, row.Execution.DeadlineAt, row.Execution.PollerName);
            await completionService.CompleteAsync(
                row.Execution,
                row.Name,
                RunbookExecutionStatus.TimedOut,
                exitCode: null,
                output: row.Execution.Output,
                error: $"No result was reported before the deadline at {row.Execution.DeadlineAt:u}. "
                    + "The runbook may or may not have run; nothing was retried.",
                now,
                cancellationToken);
        }

        return overdue.Count;
    }
}

/// <summary>
/// Looks for executions nobody answered for. Nothing else can notice: silence is the only symptom.
/// </summary>
[DisallowConcurrentExecution]
public sealed class RunbookTimeoutJob(IRunbookTimeoutSweeper sweeper) : IJob
{
    public Task Execute(IJobExecutionContext context) => sweeper.SweepAsync(context.CancellationToken);
}
