using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Monitoring.Data;

using Platform.Auditing;
using Platform.Data;
using Platform.Notifications;

namespace Modules.Monitoring.Features.Runbooks;

public interface IRunbookCompletionService
{
    /// <summary>
    /// Finishes one execution: writes the terminal row, publishes the fact, escalates if it went badly,
    /// and audits. The execution must be tracked and must still be <c>Dispatched</c>; the caller has
    /// already decided that.
    /// </summary>
    Task<RunbookExecutionResponse> CompleteAsync(
        RunbookExecution execution,
        string runbookName,
        RunbookExecutionStatus status,
        int? exitCode,
        string? output,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// The one place an execution reaches a terminal state, shared by the poller's result report and the
/// timeout sweeper.
/// <para>
/// Shared deliberately. "The result reaches the ticket, the audit trail and — when it failed — a human"
/// has to be true of a runbook that timed out exactly as much as of one that answered, and two code
/// paths writing terminal rows is how one of them quietly stops doing the third thing.
/// </para>
/// </summary>
public sealed class RunbookCompletionService(
    MonitoringDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService,
    INotificationRouter notificationRouter,
    ILogger<RunbookCompletionService> logger) : IRunbookCompletionService
{
    public async Task<RunbookExecutionResponse> CompleteAsync(
        RunbookExecution execution,
        string runbookName,
        RunbookExecutionStatus status,
        int? exitCode,
        string? output,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var before = RunbookMapping.Map(execution, runbookName);
        execution.Status = status;
        execution.ExitCode = exitCode;
        execution.Output = output;
        execution.Error = error;
        execution.CompletedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = RunbookMapping.Map(execution, runbookName);
        var startedAt = execution.DispatchedAt ?? execution.RequestedAt;

        // Published before the audit write, because the audit write is what commits the outbox — the
        // same ordering WP-3.5's alert engine uses and for the same reason.
        await publishEndpoint.Publish(
            new RunbookExecutionCompleted(
                Guid.CreateVersion7(),
                now,
                execution.Id,
                execution.RunbookId,
                execution.RunbookKey,
                runbookName,
                execution.RunbookVersion,
                execution.AlertId,
                execution.DeviceId,
                execution.CiId,
                execution.RuleId,
                status.ToString(),
                exitCode,
                output,
                error,
                execution.RequestedBy,
                execution.PollerName,
                execution.RequestedAt,
                now,
                (long)(now - startedAt).TotalSeconds),
            cancellationToken);

        if (status is not RunbookExecutionStatus.Succeeded)
        {
            await EscalateAsync(execution, runbookName, status, error, cancellationToken);
        }
        else
        {
            logger.LogInformation(
                "Runbook '{RunbookKey}' succeeded on device {DeviceId} (execution {ExecutionId}).",
                execution.RunbookKey, execution.DeviceId, execution.Id);
        }

        await auditService.WriteAsync(
            RunbookMapping.SystemActor(),
            status.ToString(),
            "RunbookExecution",
            execution.Id.ToString(),
            before,
            response,
            cancellationToken);
        return response;
    }

    /// <summary>
    /// The "failure → escalate to a human" half, on the monitoring side. The other half is the ticket,
    /// which Helpdesk writes from the event above — this is the part that reaches somebody who is not
    /// looking at a ticket queue.
    /// <para>
    /// Routed rather than emailed directly, so the WP-3.10 rules and per-person preferences apply, and
    /// keyed on the execution so an operations channel gets one message per failed run rather than one
    /// per delivery attempt.
    /// </para>
    /// </summary>
    private async Task EscalateAsync(
        RunbookExecution execution,
        string runbookName,
        RunbookExecutionStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            "Runbook '{RunbookKey}' {Status} on device {DeviceId} (execution {ExecutionId}): {Error}",
            execution.RunbookKey, status, execution.DeviceId, execution.Id, error ?? "no detail reported");

        var pollerGroup = await dbContext.MonitoredDevices.AsNoTracking()
            .Where(device => device.Id == execution.DeviceId)
            .Select(device => device.PollerGroup)
            .SingleOrDefaultAsync(cancellationToken);

        var headline = status is RunbookExecutionStatus.TimedOut
            ? $"Automated remediation timed out: {runbookName}"
            : $"Automated remediation failed: {runbookName}";

        await notificationRouter.RouteAsync(
            new NotificationEnvelope(
                nameof(RunbookExecutionCompleted),
                NotificationSeverity.Critical,
                headline,
                status is RunbookExecutionStatus.TimedOut
                    ? "The poller did not report a result before the runbook's deadline. Nothing was retried; a human has to take it from here."
                    : "The runbook ran and reported failure. Nothing was retried; a human has to take it from here.",
                DeviceGroup: pollerGroup,
                DedupeKey: $"runbook:{execution.Id}",
                Facts:
                [
                    new NotificationFact("Runbook", execution.RunbookKey),
                    new NotificationFact("Outcome", status.ToString()),
                    new NotificationFact("Device", execution.DeviceId.ToString()),
                    new NotificationFact("Requested by", execution.RequestedBy),
                    new NotificationFact("Poller", execution.PollerName ?? "none claimed it"),
                    new NotificationFact("Exit code", execution.ExitCode?.ToString() ?? "none"),
                    new NotificationFact("Detail", Summarise(error)),
                ]),
            userIds: null,
            cancellationToken);
    }

    /// <summary>
    /// One line of the failure, for a notification. The full text is on the ticket and in the execution
    /// row; a Teams card carrying eight kilobytes of stderr is one nobody can read on a phone.
    /// </summary>
    private static string Summarise(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "no detail reported";
        }

        var firstLine = error.Split('\n', 2)[0].Trim();
        return firstLine.Length <= 200 ? firstLine : firstLine[..199] + "…";
    }
}
