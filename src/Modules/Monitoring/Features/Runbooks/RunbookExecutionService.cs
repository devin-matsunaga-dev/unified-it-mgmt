using System.Security.Claims;

using Contracts.Events;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;

using Platform.Auditing;

namespace Modules.Monitoring.Features.Runbooks;

public interface IRunbookExecutionService
{
    Task<RunbookExecutionPageResponse> ListAsync(
        RunbookExecutionListRequest request, CancellationToken cancellationToken);

    Task<RunbookExecutionResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>An operator asking for a runbook by hand, against a device they name.</summary>
    Task<RunbookExecutionResult> RequestAsync(
        string key, RunRunbookRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    /// <summary>
    /// A raised alert, matched against every enabled trigger. Returns how many executions it started,
    /// which is what the consumer logs — nothing here throws for a runbook that was refused, because a
    /// refusal is the automation working.
    /// </summary>
    Task<int> TriggerAsync(AlertRaised alert, CancellationToken cancellationToken);
}

/// <summary>
/// Deciding whether a runbook may run, and writing the row that says it was asked for.
/// <para>
/// Nothing here dispatches anything. An execution is created <c>Pending</c> and a poller comes and
/// takes it, which is the shape ARCHITECTURE §4 requires: pollers have publish-only bus credentials and
/// one read-only config queue, and never consume commands. A command queue aimed at an agent would have
/// been the obvious design and is the one the architecture rules out.
/// </para>
/// </summary>
public sealed class RunbookExecutionService(
    MonitoringDbContext dbContext,
    IAuditService auditService,
    IOptions<RunbookOptions> options,
    ILogger<RunbookExecutionService> logger) : IRunbookExecutionService
{
    private const int MaximumPageSize = 200;

    public async Task<RunbookExecutionPageResponse> ListAsync(
        RunbookExecutionListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        var query = dbContext.RunbookExecutions.AsNoTracking();
        if (request.RunbookId is { } runbookId)
        {
            query = query.Where(execution => execution.RunbookId == runbookId);
        }

        if (request.DeviceId is { } deviceId)
        {
            query = query.Where(execution => execution.DeviceId == deviceId);
        }

        if (request.Status is { } status)
        {
            query = query.Where(execution => execution.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        // Projected flat and rebuilt in memory rather than `Include`d: WP-5.5 learned the hard way that
        // EF drops an Include the moment a query projects a different shape, and the runbook's name is
        // the only thing needed from the other table.
        var rows = await query
            .OrderByDescending(execution => execution.RequestedAt).ThenBy(execution => execution.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(execution => new { Execution = execution, execution.Runbook.Name })
            .ToListAsync(cancellationToken);

        return new(
            [.. rows.Select(row => RunbookMapping.Map(row.Execution, row.Name))], total, page, pageSize);
    }

    public async Task<RunbookExecutionResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.RunbookExecutions.AsNoTracking()
            .Where(execution => execution.Id == id)
            .Select(execution => new { Execution = execution, execution.Runbook.Name })
            .SingleOrDefaultAsync(cancellationToken) is { } row
            ? RunbookMapping.Map(row.Execution, row.Name)
            : null;

    public async Task<RunbookExecutionResult> RequestAsync(
        string key,
        RunRunbookRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The 403 the WP asks for, and it comes before the database is touched: a key the catalogue
        // does not name is refused whether or not a row happens to exist for it.
        if (RunbookCatalog.Find(key) is not { } definition)
        {
            logger.LogWarning(
                "A request to execute '{RunbookKey}' was refused: it is not an allowlisted runbook.", key);
            return new(RunbookOutcome.NotAllowlisted);
        }

        var runbook = await dbContext.Runbooks
            .SingleOrDefaultAsync(item => item.Key == definition.Key, cancellationToken);
        if (runbook is null)
        {
            return new(RunbookOutcome.NotFound);
        }

        var device = await dbContext.MonitoredDevices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.DeviceId, cancellationToken);
        if (device is null)
        {
            return new(RunbookOutcome.Invalid, Errors: RunbookMapping.Field(
                nameof(request.DeviceId), $"Device {request.DeviceId} does not exist."));
        }

        var binding = RunbookParameterRules.Bind(definition, request.Parameters);
        if (!binding.IsValid)
        {
            return new(RunbookOutcome.Invalid, Errors: binding.Errors);
        }

        var now = DateTimeOffset.UtcNow;
        var decision = await EvaluateAsync(runbook, now, isAutomatic: false, cancellationToken);
        if (!decision.IsAllowed)
        {
            await RecordRefusalAsync(actor, runbook, decision, alertId: null, cancellationToken);
            return decision.Verdict switch
            {
                RunbookVerdict.Disabled => new(RunbookOutcome.Disabled, Error: decision.Reason),
                _ => new(RunbookOutcome.RateLimited, Error: decision.Reason),
            };
        }

        return await InsertAsync(
            runbook,
            device,
            binding.Values!,
            alertId: null,
            ruleId: null,
            triggerId: null,
            actor,
            RunbookMapping.ActorId(actor),
            now,
            cancellationToken);
    }

    public async Task<int> TriggerAsync(AlertRaised alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var settings = options.Value;
        if (!settings.Enabled || !settings.AutomaticTriggersEnabled)
        {
            return 0;
        }

        if (!Enum.TryParse<AlertSeverity>(alert.Severity, ignoreCase: true, out var severity)
            || !Enum.IsDefined(severity))
        {
            // A severity this platform does not have a name for cannot be compared against a trigger's
            // minimum, and guessing high would run remediation on the strength of a string nobody
            // recognises.
            logger.LogWarning(
                "Alert {AlertId} arrived at severity '{Severity}', which is not one this platform knows; no runbook was started.",
                alert.AlertId, alert.Severity);
            return 0;
        }

        // The severities this alert satisfies, enumerated rather than compared. `MinimumSeverity` is
        // stored as a string (every enum in this schema is), so `trigger.MinimumSeverity <= severity`
        // translates to a *string* comparison in Postgres — and "Critical" sorts before "Warning", so
        // a Critical-only trigger fired on a Warning. Caught by the test written for exactly that
        // case; the ordering the enum declares is meaningless once the column is text.
        var eligible = Enum.GetValues<AlertSeverity>()
            .Where(value => value != AlertSeverity.Ok && value <= severity)
            .ToArray();

        var triggers = await dbContext.RunbookTriggers.AsNoTracking()
            .Include(trigger => trigger.Runbook)
            .Where(trigger => trigger.IsEnabled
                && trigger.Runbook.IsEnabled
                && trigger.MetricName.ToLower() == alert.MetricName.ToLower()
                && eligible.Contains(trigger.MinimumSeverity)
                && (trigger.DeviceId == null || trigger.DeviceId == alert.DeviceId))
            .OrderBy(trigger => trigger.CreatedAt).ThenBy(trigger => trigger.Id)
            .ToListAsync(cancellationToken);
        if (triggers.Count == 0)
        {
            return 0;
        }

        var device = await dbContext.MonitoredDevices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == alert.DeviceId, cancellationToken);
        if (device is null)
        {
            logger.LogWarning(
                "Alert {AlertId} names device {DeviceId}, which no longer exists; no runbook was started.",
                alert.AlertId, alert.DeviceId);
            return 0;
        }

        var systemActor = RunbookMapping.SystemActor();
        var now = DateTimeOffset.UtcNow;
        var started = 0;
        foreach (var trigger in triggers)
        {
            var runbook = trigger.Runbook;
            if (RunbookCatalog.Find(runbook.Key) is not { } definition)
            {
                logger.LogError(
                    "Trigger {TriggerId} names runbook '{RunbookKey}', which is no longer allowlisted; it was not run.",
                    trigger.Id, runbook.Key);
                continue;
            }

            // Re-bound rather than trusted. The parameters were validated when the trigger was written,
            // and the schema may have narrowed since — a row that was legal under an older catalogue
            // must not become the way an out-of-schema value reaches an agent.
            var binding = RunbookParameterRules.Bind(
                definition, RunbookMapping.Deserialize(trigger.ParametersJson));
            if (!binding.IsValid)
            {
                logger.LogError(
                    "Trigger {TriggerId} holds parameters '{RunbookKey}' no longer accepts; it was not run.",
                    trigger.Id, runbook.Key);
                continue;
            }

            var decision = await EvaluateAsync(runbook, now, isAutomatic: true, cancellationToken);
            if (!decision.IsAllowed)
            {
                logger.LogWarning(
                    "Alert {AlertId} started no runbook '{RunbookKey}': {Reason}.",
                    alert.AlertId, runbook.Key, decision.Reason);
                await RecordRefusalAsync(systemActor, runbook, decision, alert.AlertId, cancellationToken);
                continue;
            }

            var result = await InsertAsync(
                runbook,
                device,
                binding.Values!,
                alert.AlertId,
                alert.RuleId,
                trigger.Id,
                systemActor,
                RunbookMapping.SystemActorId,
                now,
                cancellationToken);
            if (result.Outcome is RunbookOutcome.Success)
            {
                started++;
                logger.LogInformation(
                    "Alert {AlertId} ({RuleId}) requested runbook '{RunbookKey}' on device {DeviceId}.",
                    alert.AlertId, alert.RuleId, runbook.Key, alert.DeviceId);
            }
            else if (result.Outcome is RunbookOutcome.AlreadyRequested)
            {
                logger.LogInformation(
                    "Runbook '{RunbookKey}' has already run for alert {AlertId}; it was not run again.",
                    runbook.Key, alert.AlertId);
            }
        }

        return started;
    }

    /// <summary>
    /// The bound, counted from the table. See <see cref="RunbookRateLimit"/> for why it is not in Redis.
    /// </summary>
    private async Task<RunbookDecision> EvaluateAsync(
        Runbook runbook,
        DateTimeOffset now,
        bool isAutomatic,
        CancellationToken cancellationToken)
    {
        var since = RunbookRateLimit.WindowStart(runbook, now);
        var recent = await dbContext.RunbookExecutions.CountAsync(
            execution => execution.RunbookId == runbook.Id && execution.RequestedAt >= since,
            cancellationToken);
        return RunbookRateLimit.Evaluate(runbook, options.Value, recent, isAutomatic);
    }

    private async Task<RunbookExecutionResult> InsertAsync(
        Runbook runbook,
        MonitoredDevice device,
        IReadOnlyDictionary<string, string> parameters,
        Guid? alertId,
        string? ruleId,
        Guid? triggerId,
        ClaimsPrincipal actor,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var execution = new RunbookExecution
        {
            Id = Guid.CreateVersion7(),
            RunbookId = runbook.Id,
            RunbookKey = runbook.Key,
            RunbookVersion = runbook.Version,
            TriggerId = triggerId,
            AlertId = alertId,
            DeviceId = device.Id,
            CiId = device.CiId,
            RuleId = ruleId,
            ParametersJson = RunbookMapping.Serialize(parameters),
            Status = RunbookExecutionStatus.Pending,
            RequestedBy = actorId,
            RequestedAt = now,
        };

        dbContext.RunbookExecutions.Add(execution);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index on (runbook, alert) — an escalation carrying the same alert id, or a
            // redelivered event that got past the idempotency helper. Losing this insert is the
            // "no retry storm" half of the WP being enforced by the database rather than by ordering.
            dbContext.Entry(execution).State = EntityState.Detached;
            return new(RunbookOutcome.AlreadyRequested,
                Error: $"'{runbook.Key}' has already been run for this alert.");
        }

        var response = RunbookMapping.Map(execution, runbook.Name);
        await auditService.WriteAsync(
            actor, "ExecutionRequested", "RunbookExecution", execution.Id.ToString(),
            null, response, cancellationToken);
        return new(RunbookOutcome.Success, response);
    }

    /// <summary>
    /// A refusal is audited, not only logged. "The platform declined to run something on a machine" is
    /// exactly the kind of fact an incident review goes looking for, and a log line is not where it
    /// looks.
    /// </summary>
    private Task RecordRefusalAsync(
        ClaimsPrincipal actor,
        Runbook runbook,
        RunbookDecision decision,
        Guid? alertId,
        CancellationToken cancellationToken) =>
        auditService.WriteAsync(
            actor,
            "ExecutionRefused",
            "Runbook",
            runbook.Id.ToString(),
            null,
            new
            {
                runbook.Key,
                Verdict = decision.Verdict.ToString(),
                decision.Reason,
                AlertId = alertId,
            },
            cancellationToken);
}
