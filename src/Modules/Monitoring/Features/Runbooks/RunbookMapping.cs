using System.Security.Claims;
using System.Text.Json;

using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Runbooks;

/// <summary>
/// Entity-to-response mapping and the two jsonb conversions, in one place so the registry, the
/// execution service and the poller channel cannot disagree about how a parameter set is spelt.
/// </summary>
internal static class RunbookMapping
{
    /// <summary>
    /// The actor a trigger runs under. Not a person and not an end user, following WP-3.6's system
    /// actor: every audit row it writes says plainly that nobody performed it.
    /// </summary>
    public const string SystemActorId = "system:monitoring";

    public static RunbookResponse Map(Runbook runbook, IReadOnlyList<RunbookTrigger>? triggers = null)
    {
        var definition = RunbookCatalog.Find(runbook.Key);
        return new(
            runbook.Id,
            runbook.Key,
            runbook.Name,
            runbook.Description,
            runbook.Version,
            runbook.TimeoutSeconds,
            runbook.MaxExecutionsPerWindow,
            runbook.RateLimitWindowMinutes,
            runbook.IsEnabled,
            // Stated rather than assumed. A row whose key has left the catalogue — a downgrade, a
            // restored database, a runbook withdrawn in a later release — still reads, and reads as
            // what it is: a registration that can no longer execute anything.
            definition is not null,
            definition is null
                ? []
                : [.. definition.Parameters.Select(parameter => new RunbookParameterResponse(
                    parameter.Name,
                    parameter.Description,
                    parameter.IsRequired,
                    parameter.MaxLength,
                    parameter.Example))],
            triggers is null ? [] : [.. triggers.Select(Map)],
            runbook.CreatedBy,
            runbook.CreatedAt,
            runbook.UpdatedBy,
            runbook.UpdatedAt);
    }

    public static RunbookTriggerResponse Map(RunbookTrigger trigger) => new(
        trigger.Id,
        trigger.RunbookId,
        trigger.MetricName,
        trigger.MinimumSeverity.ToString(),
        trigger.DeviceId,
        Deserialize(trigger.ParametersJson),
        trigger.IsEnabled,
        trigger.CreatedBy,
        trigger.CreatedAt,
        trigger.UpdatedBy,
        trigger.UpdatedAt);

    public static RunbookExecutionResponse Map(RunbookExecution execution, string runbookName) => new(
        execution.Id,
        execution.RunbookId,
        execution.RunbookKey,
        runbookName,
        execution.RunbookVersion,
        execution.TriggerId,
        execution.AlertId,
        execution.DeviceId,
        execution.CiId,
        execution.RuleId,
        Deserialize(execution.ParametersJson),
        execution.Status.ToString(),
        execution.RequestedBy,
        execution.RequestedAt,
        execution.PollerName,
        execution.DispatchedAt,
        execution.DeadlineAt,
        execution.CompletedAt,
        execution.ExitCode,
        execution.Output,
        execution.Error);

    public static string Serialize(IReadOnlyDictionary<string, string> parameters) =>
        JsonSerializer.Serialize(parameters.ToDictionary(entry => entry.Key, entry => entry.Value));

    public static IReadOnlyDictionary<string, string> Deserialize(string parametersJson) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson) ?? [];

    public static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    public static IReadOnlyDictionary<string, string[]> Field(string name, string message) =>
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [name] = [message] };

    /// <summary>
    /// The system actor a trigger, the sweeper and the poller channel write audit rows under. Built
    /// per call rather than shared, because a <see cref="ClaimsPrincipal"/> is mutable.
    /// </summary>
    public static ClaimsPrincipal SystemActor() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, SystemActorId),
            new Claim(ClaimTypes.Name, "Monitoring"),
        ],
        "Monitoring"));
}
