using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;

using Platform.Auditing;

namespace Modules.Monitoring.Features.Runbooks;

public interface IRunbookRegistryService
{
    /// <summary>Every allowlisted runbook the catalogue knows, registered or not — the list an operator picks from.</summary>
    IReadOnlyList<RunbookDefinition> Catalogue { get; }

    Task<IReadOnlyList<RunbookResponse>> ListAsync(CancellationToken cancellationToken);

    Task<RunbookResponse?> GetAsync(string key, CancellationToken cancellationToken);

    Task<RunbookResult> CreateAsync(
        CreateRunbookRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<RunbookResult> UpdateAsync(
        string key, UpdateRunbookRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<RunbookOutcome> DeleteAsync(string key, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<RunbookTriggerResult> AddTriggerAsync(
        string key, SaveRunbookTriggerRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<RunbookTriggerResult> UpdateTriggerAsync(
        string key, Guid triggerId, SaveRunbookTriggerRequest request, ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<RunbookOutcome> DeleteTriggerAsync(
        string key, Guid triggerId, ClaimsPrincipal actor, CancellationToken cancellationToken);
}

/// <summary>
/// Administering the allowlist: which allowlisted runbooks this estate has registered, with what
/// bounds, and which alerts start them.
/// <para>
/// Every write here is checked against <see cref="RunbookCatalog"/> first. That check is the reason
/// this service exists as its own class rather than as CRUD beside the execution path — a registry
/// write and an execution are different acts by different people, and the WP puts them behind
/// different policies.
/// </para>
/// </summary>
public sealed class RunbookRegistryService(
    MonitoringDbContext dbContext,
    IAuditService auditService,
    IOptions<RunbookOptions> options) : IRunbookRegistryService
{
    public IReadOnlyList<RunbookDefinition> Catalogue => RunbookCatalog.All;

    public async Task<IReadOnlyList<RunbookResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var runbooks = await dbContext.Runbooks.AsNoTracking()
            .Include(runbook => runbook.Triggers)
            .OrderBy(runbook => runbook.Key)
            .ToListAsync(cancellationToken);
        return [.. runbooks.Select(runbook => RunbookMapping.Map(
            runbook, [.. runbook.Triggers.OrderBy(trigger => trigger.CreatedAt)]))];
    }

    public async Task<RunbookResponse?> GetAsync(string key, CancellationToken cancellationToken) =>
        await LoadAsync(key, tracking: false, cancellationToken) is { } runbook
            ? RunbookMapping.Map(runbook, [.. runbook.Triggers.OrderBy(trigger => trigger.CreatedAt)])
            : null;

    public async Task<RunbookResult> CreateAsync(
        CreateRunbookRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The allowlist check, first and unconditionally. Nothing below can create a runbook the
        // catalogue does not name, whatever the rest of the request says.
        if (RunbookCatalog.Find(request.Key) is not { } definition)
        {
            return new(RunbookOutcome.NotAllowlisted);
        }

        if (await dbContext.Runbooks.AnyAsync(item => item.Key == definition.Key, cancellationToken))
        {
            return new(RunbookOutcome.Duplicate,
                Error: $"The runbook '{definition.Key}' is already registered.");
        }

        var settings = options.Value;
        var timeout = request.TimeoutSeconds ?? definition.DefaultTimeoutSeconds;
        var allowance = request.MaxExecutionsPerWindow ?? settings.DefaultMaxExecutionsPerWindow;
        var window = request.RateLimitWindowMinutes ?? settings.DefaultRateLimitWindowMinutes;
        if (Validate(timeout, allowance, window, settings) is { } errors)
        {
            return new(RunbookOutcome.Invalid, Errors: errors);
        }

        var actorId = RunbookMapping.ActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var runbook = new Runbook
        {
            Id = Guid.CreateVersion7(),
            Key = definition.Key,
            Name = Trimmed(request.Name) ?? definition.Name,
            Description = Trimmed(request.Description) ?? definition.Description,
            Version = 1,
            TimeoutSeconds = timeout,
            MaxExecutionsPerWindow = allowance,
            RateLimitWindowMinutes = window,
            IsEnabled = request.IsEnabled,
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
        };

        dbContext.Runbooks.Add(runbook);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = RunbookMapping.Map(runbook, []);
        await auditService.WriteAsync(
            actor, "Registered", "Runbook", runbook.Id.ToString(), null, response, cancellationToken);
        return new(RunbookOutcome.Success, response);
    }

    public async Task<RunbookResult> UpdateAsync(
        string key,
        UpdateRunbookRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await LoadAsync(key, tracking: true, cancellationToken) is not { } runbook)
        {
            return new(RunbookOutcome.NotFound);
        }

        var settings = options.Value;
        var timeout = request.TimeoutSeconds ?? runbook.TimeoutSeconds;
        var allowance = request.MaxExecutionsPerWindow ?? runbook.MaxExecutionsPerWindow;
        var window = request.RateLimitWindowMinutes ?? runbook.RateLimitWindowMinutes;
        if (Validate(timeout, allowance, window, settings) is { } errors)
        {
            return new(RunbookOutcome.Invalid, Errors: errors);
        }

        var before = RunbookMapping.Map(runbook, [.. runbook.Triggers]);
        runbook.Name = Trimmed(request.Name) ?? runbook.Name;
        runbook.Description = Trimmed(request.Description) ?? runbook.Description;
        runbook.TimeoutSeconds = timeout;
        runbook.MaxExecutionsPerWindow = allowance;
        runbook.RateLimitWindowMinutes = window;
        runbook.IsEnabled = request.IsEnabled;
        // Bumped on every edit rather than only on the ones that change behaviour. An execution stamped
        // with a version has to be able to say "this definition, exactly", and deciding which fields
        // count would make that a judgement rather than a fact.
        runbook.Version++;
        runbook.UpdatedBy = RunbookMapping.ActorId(actor);
        runbook.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = RunbookMapping.Map(runbook, [.. runbook.Triggers.OrderBy(trigger => trigger.CreatedAt)]);
        await auditService.WriteAsync(
            actor, "Updated", "Runbook", runbook.Id.ToString(), before, response, cancellationToken);
        return new(RunbookOutcome.Success, response);
    }

    public async Task<RunbookOutcome> DeleteAsync(
        string key,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (await LoadAsync(key, tracking: true, cancellationToken) is not { } runbook)
        {
            return RunbookOutcome.NotFound;
        }

        // Refused rather than cascaded. The executions are the record of what this platform did to real
        // machines, and deleting a registration must not be a way to erase it — disabling the runbook
        // is the way to stop it running, and it keeps the history.
        if (await dbContext.RunbookExecutions.AnyAsync(
                execution => execution.RunbookId == runbook.Id, cancellationToken))
        {
            return RunbookOutcome.InUse;
        }

        var before = RunbookMapping.Map(runbook, [.. runbook.Triggers]);
        dbContext.Runbooks.Remove(runbook);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "Runbook", runbook.Id.ToString(), before, null, cancellationToken);
        return RunbookOutcome.Success;
    }

    public async Task<RunbookTriggerResult> AddTriggerAsync(
        string key,
        SaveRunbookTriggerRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await LoadAsync(key, tracking: true, cancellationToken) is not { } runbook)
        {
            return new(RunbookOutcome.NotFound);
        }

        var prepared = await PrepareTriggerAsync(runbook, request, triggerId: null, cancellationToken);
        if (prepared.Outcome is not RunbookOutcome.Success)
        {
            return new(prepared.Outcome, Errors: prepared.Errors, Error: prepared.Error);
        }

        var actorId = RunbookMapping.ActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var trigger = new RunbookTrigger
        {
            Id = Guid.CreateVersion7(),
            RunbookId = runbook.Id,
            MetricName = prepared.MetricName!,
            MinimumSeverity = prepared.Severity,
            DeviceId = request.DeviceId,
            ParametersJson = RunbookMapping.Serialize(prepared.Parameters!),
            IsEnabled = request.IsEnabled,
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
        };

        dbContext.RunbookTriggers.Add(trigger);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = RunbookMapping.Map(trigger);
        await auditService.WriteAsync(
            actor, "Created", "RunbookTrigger", trigger.Id.ToString(), null, response, cancellationToken);
        return new(RunbookOutcome.Success, response);
    }

    public async Task<RunbookTriggerResult> UpdateTriggerAsync(
        string key,
        Guid triggerId,
        SaveRunbookTriggerRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await LoadAsync(key, tracking: true, cancellationToken) is not { } runbook)
        {
            return new(RunbookOutcome.NotFound);
        }

        var trigger = runbook.Triggers.SingleOrDefault(item => item.Id == triggerId);
        if (trigger is null)
        {
            return new(RunbookOutcome.NotFound);
        }

        var prepared = await PrepareTriggerAsync(runbook, request, triggerId, cancellationToken);
        if (prepared.Outcome is not RunbookOutcome.Success)
        {
            return new(prepared.Outcome, Errors: prepared.Errors, Error: prepared.Error);
        }

        var before = RunbookMapping.Map(trigger);
        trigger.MetricName = prepared.MetricName!;
        trigger.MinimumSeverity = prepared.Severity;
        trigger.DeviceId = request.DeviceId;
        trigger.ParametersJson = RunbookMapping.Serialize(prepared.Parameters!);
        trigger.IsEnabled = request.IsEnabled;
        trigger.UpdatedBy = RunbookMapping.ActorId(actor);
        trigger.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = RunbookMapping.Map(trigger);
        await auditService.WriteAsync(
            actor, "Updated", "RunbookTrigger", trigger.Id.ToString(), before, response, cancellationToken);
        return new(RunbookOutcome.Success, response);
    }

    public async Task<RunbookOutcome> DeleteTriggerAsync(
        string key,
        Guid triggerId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (await LoadAsync(key, tracking: true, cancellationToken) is not { } runbook)
        {
            return RunbookOutcome.NotFound;
        }

        var trigger = runbook.Triggers.SingleOrDefault(item => item.Id == triggerId);
        if (trigger is null)
        {
            return RunbookOutcome.NotFound;
        }

        var before = RunbookMapping.Map(trigger);
        dbContext.RunbookTriggers.Remove(trigger);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "RunbookTrigger", trigger.Id.ToString(), before, null, cancellationToken);
        return RunbookOutcome.Success;
    }

    /// <summary>
    /// Everything a trigger write has to be true about, in one place, because add and update have to
    /// agree exactly — a rule enforced on creation and not on edit is a rule with a way round it.
    /// </summary>
    private async Task<PreparedTrigger> PrepareTriggerAsync(
        Runbook runbook,
        SaveRunbookTriggerRequest request,
        Guid? triggerId,
        CancellationToken cancellationToken)
    {
        if (RunbookCatalog.Find(runbook.Key) is not { } definition)
        {
            // The registration survives a key leaving the catalogue; a trigger for it does not get
            // written, because that would be arranging for something unrunnable to be attempted.
            return PreparedTrigger.Failed(RunbookOutcome.NotAllowlisted);
        }

        var metricName = request.MetricName?.Trim();
        if (string.IsNullOrEmpty(metricName) || metricName.Length > 100)
        {
            return PreparedTrigger.Failed(RunbookOutcome.Invalid, RunbookMapping.Field(
                nameof(request.MetricName),
                "A trigger names the metric whose alert fires it, of at most 100 characters."));
        }

        // The standing `Enum.TryParse` hazard, met for the fourth time and for the first time as a
        // security control rather than a correctness one: `TryParse` accepts "5" for a severity and
        // would store a trigger that matches everything or nothing depending on which number it landed on.
        if (!Enum.TryParse<AlertSeverity>(request.MinimumSeverity, ignoreCase: true, out var severity)
            || !Enum.IsDefined(severity)
            || severity == AlertSeverity.Ok)
        {
            return PreparedTrigger.Failed(RunbookOutcome.Invalid, RunbookMapping.Field(
                nameof(request.MinimumSeverity),
                "Minimum severity must be Warning or Critical. Ok is not a severity an alert is raised at."));
        }

        if (request.DeviceId is { } deviceId
            && !await dbContext.MonitoredDevices.AnyAsync(device => device.Id == deviceId, cancellationToken))
        {
            return PreparedTrigger.Failed(RunbookOutcome.Invalid, RunbookMapping.Field(
                nameof(request.DeviceId), $"Device {deviceId} does not exist."));
        }

        var binding = RunbookParameterRules.Bind(definition, request.Parameters);
        if (!binding.IsValid)
        {
            return PreparedTrigger.Failed(RunbookOutcome.Invalid, binding.Errors);
        }

        // One trigger per runbook per metric per scope. A second differing only in severity would fire
        // twice for one alert, and only the unique-per-alert index would stop the second — which is a
        // constraint doing the work a configuration mistake should have been refused for.
        var clash = await dbContext.RunbookTriggers.AnyAsync(
            item => item.RunbookId == runbook.Id
                && item.MetricName == metricName
                && item.DeviceId == request.DeviceId
                && (triggerId == null || item.Id != triggerId),
            cancellationToken);
        if (clash)
        {
            return PreparedTrigger.Failed(
                RunbookOutcome.Duplicate,
                error: $"'{runbook.Key}' already has a trigger for '{metricName}' on that scope.");
        }

        return new PreparedTrigger(
            RunbookOutcome.Success, metricName, severity, binding.Values, null, null);
    }

    private Task<Runbook?> LoadAsync(string key, bool tracking, CancellationToken cancellationToken)
    {
        var canonical = RunbookCatalog.Canonicalise(key) ?? key?.Trim() ?? string.Empty;
        var query = tracking ? dbContext.Runbooks : dbContext.Runbooks.AsNoTracking();
        return query.Include(runbook => runbook.Triggers)
            .SingleOrDefaultAsync(runbook => runbook.Key == canonical, cancellationToken);
    }

    private static IReadOnlyDictionary<string, string[]>? Validate(
        int timeoutSeconds,
        int maxExecutionsPerWindow,
        int rateLimitWindowMinutes,
        RunbookOptions settings)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (timeoutSeconds < 1 || timeoutSeconds > settings.MaximumTimeoutSeconds)
        {
            errors[nameof(CreateRunbookRequest.TimeoutSeconds)] =
                [$"A runbook may run for between 1 and {settings.MaximumTimeoutSeconds} seconds."];
        }

        if (maxExecutionsPerWindow < 1)
        {
            errors[nameof(CreateRunbookRequest.MaxExecutionsPerWindow)] =
                ["A runbook must be allowed at least one execution per window. Disable it to stop it running."];
        }

        if (rateLimitWindowMinutes < 1)
        {
            errors[nameof(CreateRunbookRequest.RateLimitWindowMinutes)] =
                ["The rate-limit window must be at least one minute."];
        }

        return errors.Count > 0 ? errors : null;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PreparedTrigger(
        RunbookOutcome Outcome,
        string? MetricName,
        AlertSeverity Severity,
        IReadOnlyDictionary<string, string>? Parameters,
        IReadOnlyDictionary<string, string[]>? Errors,
        string? Error)
    {
        public static PreparedTrigger Failed(
            RunbookOutcome outcome,
            IReadOnlyDictionary<string, string[]>? errors = null,
            string? error = null) =>
            new(outcome, null, AlertSeverity.Critical, null, errors, error);
    }
}
