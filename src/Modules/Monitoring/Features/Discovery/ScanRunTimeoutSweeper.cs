using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Monitoring.Data;

using Quartz;

namespace Modules.Monitoring.Features.Discovery;

public interface IScanRunTimeoutSweeper
{
    /// <summary>Finishes every running scan whose deadline has passed. Returns how many.</summary>
    Task<int> SweepAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The platform's side of a requested scan's timeout.
/// <para>
/// It exists because of the thing WP-4.1 recorded about this service and never fixed: the discovery
/// scanner has no heartbeat and no registration, so a crashed one is completely silent. Without this,
/// a run claimed by a scanner that then died would sit as <c>Running</c> for ever, and the page an
/// operator is watching would show a scan in progress that nothing is doing.
/// </para>
/// <para>
/// A timed-out run is never re-dispatched. Unlike a runbook this is safe to repeat — a sweep changes
/// nothing on the estate — but repeating it automatically would hide the dead scanner behind a run
/// that eventually succeeds, and the queue filling again is the only evidence anybody gets.
/// </para>
/// </summary>
public sealed class ScanRunTimeoutSweeper(
    MonitoringDbContext dbContext,
    ILogger<ScanRunTimeoutSweeper> logger) : IScanRunTimeoutSweeper
{
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var overdue = await dbContext.ScanRuns
            .Where(run => run.Status == ScanRunStatus.Running
                && run.DeadlineAt != null
                && run.DeadlineAt < now)
            .ToListAsync(cancellationToken);
        if (overdue.Count == 0)
        {
            return 0;
        }

        foreach (var run in overdue)
        {
            logger.LogWarning(
                "Scan run {ScanRunId} of profile '{ScanProfileName}' passed its deadline of {DeadlineAt:u} with no result from {DiscoveryName}.",
                run.Id, run.ScanProfileName, run.DeadlineAt, run.DiscoveryName);

            run.Status = ScanRunStatus.TimedOut;
            run.CompletedAt = now;
            run.Error = $"No result was reported before the deadline at {run.DeadlineAt:u}. "
                + $"The scan may or may not have run; '{run.DiscoveryName}' may be down.";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return overdue.Count;
    }
}

/// <summary>
/// Looks for scans nobody reported on. Nothing else can notice — this service has no heartbeat, so
/// silence is the only symptom a dead scanner produces.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ScanRunTimeoutJob(IScanRunTimeoutSweeper sweeper) : IJob
{
    public Task Execute(IJobExecutionContext context) => sweeper.SweepAsync(context.CancellationToken);
}
