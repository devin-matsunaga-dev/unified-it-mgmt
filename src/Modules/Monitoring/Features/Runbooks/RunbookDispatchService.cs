using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

using Platform.Auditing;
using Platform.Integration;

namespace Modules.Monitoring.Features.Runbooks;

public interface IRunbookDispatchService
{
    /// <summary>
    /// Hands a poller the executions waiting for its group, and marks them as its own. This is the only
    /// place anything is ever told to run a runbook, and it answers a <em>fetch</em> — the poller asks,
    /// the platform never pushes.
    /// </summary>
    Task<RunbookDispatchResult> ClaimAsync(string pollerName, CancellationToken cancellationToken);

    Task<RunbookReportResult> ReportAsync(
        string pollerName,
        Guid executionId,
        ReportRunbookResultRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The agent side of the channel, and the reason there is no queue.
/// <para>
/// ARCHITECTURE §4 gives a poller publish-only bus credentials plus one read-only config queue and says
/// pollers never consume commands. An execution therefore travels the way configuration already does:
/// the poller asks over HTTP under its own <c>CanPoll</c> identity, gets what is waiting for its group,
/// and posts the result back. Nothing new is granted to the agent — it cannot create an execution, only
/// collect one the server decided on and report what happened.
/// </para>
/// <para>
/// Two pollers may share a group, so claiming is a conditional update rather than a read followed by a
/// write: the row's status and poller name move in one statement, and a poller that lost the race sees
/// a row carrying somebody else's name and leaves it alone.
/// </para>
/// </summary>
public sealed class RunbookDispatchService(
    MonitoringDbContext dbContext,
    ICiDirectory ciDirectory,
    IRunbookCompletionService completionService,
    IAuditService auditService,
    IOptions<RunbookOptions> options,
    ILogger<RunbookDispatchService> logger) : IRunbookDispatchService
{
    public async Task<RunbookDispatchResult> ClaimAsync(
        string pollerName,
        CancellationToken cancellationToken)
    {
        var name = pollerName.Trim();
        var poller = await dbContext.Pollers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Name == name, cancellationToken);
        if (poller is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var settings = options.Value;
        var now = DateTimeOffset.UtcNow;
        if (!settings.Enabled || !poller.IsEnabled)
        {
            // An empty list rather than an error. The kill switch has to look to an agent exactly like
            // "nothing to do", or a poller would log an error every cycle for as long as it was off.
            return new(MonitoringOutcome.Success, Empty(poller, now));
        }

        var devicesInGroup = dbContext.MonitoredDevices
            .Where(device => device.PollerGroup == poller.PollerGroup)
            .Select(device => device.Id);

        var candidateIds = await dbContext.RunbookExecutions
            .Where(execution => execution.Status == RunbookExecutionStatus.Pending
                && devicesInGroup.Contains(execution.DeviceId))
            .OrderBy(execution => execution.RequestedAt).ThenBy(execution => execution.Id)
            .Take(Math.Max(settings.DispatchBatchSize, 1))
            .Select(execution => execution.Id)
            .ToListAsync(cancellationToken);
        if (candidateIds.Count == 0)
        {
            return new(MonitoringOutcome.Success, Empty(poller, now));
        }

        // `Status == Pending` inside the update is the whole of the concurrency control: whichever
        // statement runs first moves the row, the other matches nothing.
        await dbContext.RunbookExecutions
            .Where(execution => candidateIds.Contains(execution.Id)
                && execution.Status == RunbookExecutionStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(execution => execution.Status, RunbookExecutionStatus.Dispatched)
                    .SetProperty(execution => execution.PollerName, name)
                    .SetProperty(execution => execution.DispatchedAt, now),
                cancellationToken);

        // Read back what this poller actually holds. A row now carrying another poller's name was lost
        // fairly and is not in this answer.
        var claimed = await dbContext.RunbookExecutions
            .Where(execution => candidateIds.Contains(execution.Id)
                && execution.PollerName == name
                && execution.Status == RunbookExecutionStatus.Dispatched)
            .Select(execution => new { Execution = execution, execution.Runbook.TimeoutSeconds })
            .ToListAsync(cancellationToken);
        if (claimed.Count == 0)
        {
            return new(MonitoringOutcome.Success, Empty(poller, now));
        }

        // The deadline is per runbook, so it is stamped after the claim rather than in it.
        foreach (var row in claimed)
        {
            row.Execution.DeadlineAt = now.AddSeconds(row.TimeoutSeconds);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var deviceIds = claimed.Select(row => row.Execution.DeviceId).Distinct().ToList();
        var devices = await dbContext.MonitoredDevices.AsNoTracking()
            .Where(device => deviceIds.Contains(device.Id))
            .Select(device => new { device.Id, device.CiId, device.Address })
            .ToListAsync(cancellationToken);
        var addressesById = devices.ToDictionary(device => device.Id);
        // The CI name travels so the agent's log names the device an operator knows, exactly as the
        // config fetch does — and through the port, so Monitoring still never reads the assets schema.
        var names = (await ciDirectory.GetSummariesAsync(
                [.. devices.Select(device => device.CiId).Distinct()], cancellationToken))
            .ToDictionary(summary => summary.Id, summary => summary.Name);

        var items = new List<RunbookDispatchItem>(claimed.Count);
        foreach (var row in claimed.OrderBy(row => row.Execution.RequestedAt))
        {
            if (!addressesById.TryGetValue(row.Execution.DeviceId, out var device))
            {
                continue;
            }

            items.Add(new RunbookDispatchItem(
                row.Execution.Id,
                row.Execution.RunbookKey,
                row.Execution.RunbookVersion,
                row.Execution.DeviceId,
                row.Execution.CiId,
                names.GetValueOrDefault(row.Execution.CiId),
                device.Address,
                RunbookMapping.Deserialize(row.Execution.ParametersJson),
                row.TimeoutSeconds,
                row.Execution.DeadlineAt!.Value));
        }

        logger.LogInformation(
            "Poller {PollerName} claimed {Count} runbook execution(s).", name, items.Count);
        // Handing an agent something to run on a machine is an act, not bookkeeping — unlike the config
        // fetch beside it, which records only which version a poller now holds. It is audited under the
        // system actor rather than the poller's token, because the platform decided this, not the agent.
        await auditService.WriteAsync(
            RunbookMapping.SystemActor(),
            "Dispatched",
            "Poller",
            poller.Id.ToString(),
            null,
            new
            {
                Poller = name,
                poller.PollerGroup,
                Executions = items.Select(item => new { item.ExecutionId, item.RunbookKey }).ToList(),
            },
            cancellationToken);

        return new(MonitoringOutcome.Success, new RunbookDispatchResponse(
            poller.Name, poller.PollerGroup, items, now));
    }

    public async Task<RunbookReportResult> ReportAsync(
        string pollerName,
        Guid executionId,
        ReportRunbookResultRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = pollerName.Trim();
        // The same closed-enum discipline WP-5.5 applied to widget widths — and the fourth meeting
        // with this hazard is the one that shows `Enum.IsDefined` is not enough on its own. `TryParse`
        // accepts the string "3", and 3 *is* a defined member, so an agent posting a digit would have
        // its result recorded as whatever member happened to sit at that ordinal. The name comparison
        // is what closes it: an outcome has to be spelt, not numbered.
        var outcome = request.Outcome?.Trim();
        if (!Enum.TryParse<RunbookExecutionStatus>(outcome, ignoreCase: true, out var status)
            || !Enum.IsDefined(status)
            || !string.Equals(status.ToString(), outcome, StringComparison.OrdinalIgnoreCase)
            || status is RunbookExecutionStatus.Pending or RunbookExecutionStatus.Dispatched)
        {
            return new(MonitoringOutcome.Invalid, Errors: RunbookMapping.Field(
                nameof(request.Outcome),
                "Outcome must be Succeeded, Failed or TimedOut."));
        }

        var row = await dbContext.RunbookExecutions
            .Where(execution => execution.Id == executionId)
            .Select(execution => new { Execution = execution, execution.Runbook.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null || row.Execution.PollerName != name)
        {
            // A poller reporting on an execution that is not its own is told the same thing as one
            // reporting on an execution that does not exist. Which of the two it was is not an agent's
            // business, and the difference is in the log.
            logger.LogWarning(
                "Poller {PollerName} reported a result for execution {ExecutionId}, which is not one it holds.",
                name, executionId);
            return new(MonitoringOutcome.NotFound);
        }

        if (row.Execution.Status is not RunbookExecutionStatus.Dispatched)
        {
            // Already finished — the sweeper timed it out, or this is a repeat of a report that landed.
            // A conflict rather than a silent overwrite: the first terminal state is the true one, and
            // the agent treats this as "already recorded" and stops asking.
            return new(MonitoringOutcome.Duplicate,
                Execution: RunbookMapping.Map(row.Execution, row.Name),
                Error: $"Execution {executionId} was already recorded as {row.Execution.Status}.");
        }

        var settings = options.Value;
        var response = await completionService.CompleteAsync(
            row.Execution,
            row.Name,
            status,
            request.ExitCode,
            Truncate(request.Output, settings.MaximumOutputCharacters),
            Truncate(request.Error, settings.MaximumOutputCharacters),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    private static RunbookDispatchResponse Empty(Poller poller, DateTimeOffset now) =>
        new(poller.Name, poller.PollerGroup, [], now);

    /// <summary>
    /// Bounded on the way in rather than on the way out. This text is written onto a ticket verbatim and
    /// republished on an event, so the place to cap it is before it is stored.
    /// </summary>
    private static string? Truncate(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= maximum
            ? value
            : value[..maximum] + $"\n… truncated at {maximum} characters.";
    }
}
