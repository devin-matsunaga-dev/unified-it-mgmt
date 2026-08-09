using System.Security.Claims;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.PollerConfig;
using Platform.Auditing;
using Platform.Integration;

namespace Modules.Monitoring.Features.Devices;

public sealed class MonitoredDeviceService(
    MonitoringDbContext dbContext,
    ICiDirectory ciDirectory,
    IMonitoringConfigLog configLog,
    IAuditService auditService) : IMonitoredDeviceService
{
    /// <summary>Where a device lands when the caller does not say which poller owns it.</summary>
    public const string DefaultPollerGroup = "default";

    private const int MaximumPageSize = 200;

    public async Task<MonitoredDevicePageResponse> ListAsync(
        MonitoredDeviceListRequest request,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        var query = dbContext.MonitoredDevices.AsNoTracking();
        if (request.CiId is { } ciId)
        {
            query = query.Where(device => device.CiId == ciId);
        }

        if (request.IsEnabled is { } isEnabled)
        {
            query = query.Where(device => device.IsEnabled == isEnabled);
        }

        if (!string.IsNullOrWhiteSpace(request.PollerGroup))
        {
            query = query.Where(device => device.PollerGroup == request.PollerGroup);
        }

        // Search covers the address only. A CI's name lives in the Assets schema, which this module
        // may not query, and the read port answers by id rather than by search term.
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(device => EF.Functions.ILike(device.Address, term));
        }

        var total = await query.CountAsync(cancellationToken);
        var devices = await query
            .OrderBy(device => device.Address).ThenBy(device => device.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(device => new
            {
                Device = device,
                CheckCount = device.Checks.Count,
            })
            .ToListAsync(cancellationToken);

        var summaries = await ResolveCisAsync(
            devices.Select(entry => entry.Device.CiId).ToList(), cancellationToken);

        return new(
            [.. devices.Select(entry => Map(entry.Device, entry.CheckCount, summaries))],
            total,
            page,
            pageSize);
    }

    public async Task<MonitoredDeviceResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await dbContext.MonitoredDevices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var checkCount = await dbContext.CheckDefinitions
            .CountAsync(check => check.DeviceId == id, cancellationToken);
        return Map(device, checkCount, await ResolveCisAsync([device.CiId], cancellationToken));
    }

    public async Task<MonitoredDeviceResult> CreateAsync(
        CreateMonitoredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        // The CI has to exist before anything is monitored as it; the port is the only way this
        // module is allowed to ask.
        var summaries = await ResolveCisAsync([request.CiId], cancellationToken);
        if (!summaries.ContainsKey(request.CiId))
        {
            return new(MonitoringOutcome.Invalid, Errors: Field(
                nameof(request.CiId), $"CI '{request.CiId}' does not exist."));
        }

        if (await dbContext.MonitoredDevices.AnyAsync(device => device.CiId == request.CiId, cancellationToken))
        {
            return new(
                MonitoringOutcome.Duplicate,
                Error: $"CI '{summaries[request.CiId].Name}' is already monitored.");
        }

        var actorId = GetActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var device = new MonitoredDevice
        {
            Id = Guid.CreateVersion7(),
            CiId = request.CiId,
            Address = request.Address.Trim(),
            PollerGroup = NormalisePollerGroup(request.PollerGroup),
            IsEnabled = request.IsEnabled,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.MonitoredDevices.Add(device);
        await configLog.RecordAsync(
            MonitoringConfigEntity.Device, device.Id, device.Id, device.PollerGroup,
            MonitoringConfigChangeKind.Upserted, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(device, checkCount: 0, summaries);
        await auditService.WriteAsync(
            actor, "Created", "MonitoredDevice", device.Id.ToString(), null, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<MonitoredDeviceResult> UpdateAsync(
        Guid id,
        UpdateMonitoredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.MonitoredDevices.SingleOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (device is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var summaries = await ResolveCisAsync([device.CiId], cancellationToken);
        var checkCount = await dbContext.CheckDefinitions
            .CountAsync(check => check.DeviceId == id, cancellationToken);
        var before = Map(device, checkCount, summaries);
        var previousGroup = device.PollerGroup;

        device.Address = request.Address.Trim();
        device.PollerGroup = NormalisePollerGroup(request.PollerGroup);
        device.IsEnabled = request.IsEnabled;
        device.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        device.UpdatedBy = GetActorId(actor);
        device.UpdatedAt = DateTimeOffset.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await configLog.RecordAsync(
            MonitoringConfigEntity.Device, device.Id, device.Id, device.PollerGroup,
            MonitoringConfigChangeKind.Upserted, cancellationToken);

        // A device that changed hands leaves a change against the group it left, or that group's
        // poller would keep polling a device that is no longer its responsibility.
        if (!string.Equals(previousGroup, device.PollerGroup, StringComparison.Ordinal))
        {
            await configLog.RecordAsync(
                MonitoringConfigEntity.Device, device.Id, device.Id, previousGroup,
                MonitoringConfigChangeKind.Removed, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(device, checkCount, summaries);
        await auditService.WriteAsync(
            actor, "Updated", "MonitoredDevice", device.Id.ToString(), before, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<MonitoringOutcome> DeleteAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.MonitoredDevices.SingleOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (device is null)
        {
            return MonitoringOutcome.NotFound;
        }

        var checkCount = await dbContext.CheckDefinitions
            .CountAsync(check => check.DeviceId == id, cancellationToken);
        var before = Map(device, checkCount, await ResolveCisAsync([device.CiId], cancellationToken));

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.MonitoredDevices.Remove(device);
        await configLog.RecordAsync(
            MonitoringConfigEntity.Device, device.Id, device.Id, device.PollerGroup,
            MonitoringConfigChangeKind.Removed, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "MonitoredDevice", id.ToString(), before, null, cancellationToken);
        return MonitoringOutcome.Success;
    }

    public async Task<IReadOnlyList<CheckResponse>?> ListChecksAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.MonitoredDevices.AnyAsync(device => device.Id == deviceId, cancellationToken))
        {
            return null;
        }

        var checks = await dbContext.CheckDefinitions.AsNoTracking()
            .Where(check => check.DeviceId == deviceId)
            .OrderBy(check => check.Name).ThenBy(check => check.Id)
            .ToListAsync(cancellationToken);
        return [.. checks.Select(Map)];
    }

    public async Task<CheckResult> CreateCheckAsync(
        Guid deviceId,
        CreateCheckRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.MonitoredDevices.SingleOrDefaultAsync(
            item => item.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var parameters = Normalise(request.Parameters);
        var errors = CheckRules.Validate(
            request.Type, request.IntervalSeconds, request.TimeoutSeconds,
            request.WarningThreshold, request.CriticalThreshold, request.Comparison, parameters);
        if (errors.Count > 0)
        {
            return new(MonitoringOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.CheckDefinitions.AnyAsync(
                check => check.DeviceId == deviceId && check.Name == name, cancellationToken))
        {
            return new(MonitoringOutcome.Duplicate, Error: $"This device already has a check named '{name}'.");
        }

        var actorId = GetActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var check = new CheckDefinition
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            Type = request.Type,
            Name = name,
            IntervalSeconds = request.IntervalSeconds,
            TimeoutSeconds = request.TimeoutSeconds,
            WarningThreshold = request.WarningThreshold,
            CriticalThreshold = request.CriticalThreshold,
            Comparison = request.Comparison,
            ParametersJson = JsonSerializer.Serialize(parameters),
            IsEnabled = request.IsEnabled,
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.CheckDefinitions.Add(check);
        await configLog.RecordAsync(
            MonitoringConfigEntity.Check, check.Id, deviceId, device.PollerGroup,
            MonitoringConfigChangeKind.Upserted, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(check);
        await auditService.WriteAsync(
            actor, "Created", "CheckDefinition", check.Id.ToString(), null, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<CheckResult> UpdateCheckAsync(
        Guid checkId,
        UpdateCheckRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var check = await dbContext.CheckDefinitions
            .Include(item => item.Device)
            .SingleOrDefaultAsync(item => item.Id == checkId, cancellationToken);
        if (check is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var parameters = Normalise(request.Parameters);
        var errors = CheckRules.Validate(
            check.Type, request.IntervalSeconds, request.TimeoutSeconds,
            request.WarningThreshold, request.CriticalThreshold, request.Comparison, parameters);
        if (errors.Count > 0)
        {
            return new(MonitoringOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.CheckDefinitions.AnyAsync(
                item => item.DeviceId == check.DeviceId && item.Name == name && item.Id != checkId,
                cancellationToken))
        {
            return new(MonitoringOutcome.Duplicate, Error: $"This device already has a check named '{name}'.");
        }

        var before = Map(check);
        check.Name = name;
        check.IntervalSeconds = request.IntervalSeconds;
        check.TimeoutSeconds = request.TimeoutSeconds;
        check.WarningThreshold = request.WarningThreshold;
        check.CriticalThreshold = request.CriticalThreshold;
        check.Comparison = request.Comparison;
        check.ParametersJson = JsonSerializer.Serialize(parameters);
        check.IsEnabled = request.IsEnabled;
        check.UpdatedBy = GetActorId(actor);
        check.UpdatedAt = DateTimeOffset.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await configLog.RecordAsync(
            MonitoringConfigEntity.Check, check.Id, check.DeviceId, check.Device.PollerGroup,
            MonitoringConfigChangeKind.Upserted, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(check);
        await auditService.WriteAsync(
            actor, "Updated", "CheckDefinition", check.Id.ToString(), before, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<MonitoringOutcome> DeleteCheckAsync(
        Guid checkId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var check = await dbContext.CheckDefinitions
            .Include(item => item.Device)
            .SingleOrDefaultAsync(item => item.Id == checkId, cancellationToken);
        if (check is null)
        {
            return MonitoringOutcome.NotFound;
        }

        var before = Map(check);
        var deviceId = check.DeviceId;
        var pollerGroup = check.Device.PollerGroup;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.CheckDefinitions.Remove(check);
        await configLog.RecordAsync(
            MonitoringConfigEntity.Check, checkId, deviceId, pollerGroup,
            MonitoringConfigChangeKind.Removed, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "CheckDefinition", checkId.ToString(), before, null, cancellationToken);
        return MonitoringOutcome.Success;
    }

    private async Task<IReadOnlyDictionary<Guid, CiSummary>> ResolveCisAsync(
        IReadOnlyCollection<Guid> ciIds,
        CancellationToken cancellationToken)
    {
        if (ciIds.Count == 0)
        {
            return new Dictionary<Guid, CiSummary>();
        }

        var summaries = await ciDirectory.GetSummariesAsync(
            [.. ciIds.Distinct()], cancellationToken);
        return summaries.ToDictionary(summary => summary.Id);
    }

    /// <summary>Group names are matched exactly by the config fetch, so they are trimmed once here.</summary>
    private static string NormalisePollerGroup(string? pollerGroup) =>
        string.IsNullOrWhiteSpace(pollerGroup) ? DefaultPollerGroup : pollerGroup.Trim();

    private static IReadOnlyDictionary<string, string> Normalise(
        IReadOnlyDictionary<string, string>? parameters) =>
        parameters is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : parameters.ToDictionary(entry => entry.Key.Trim(), entry => entry.Value, StringComparer.Ordinal);

    private static MonitoredDeviceResponse Map(
        MonitoredDevice device,
        int checkCount,
        IReadOnlyDictionary<Guid, CiSummary> summaries)
    {
        summaries.TryGetValue(device.CiId, out var ci);
        return new(
            device.Id,
            device.CiId,
            ci?.Name,
            ci?.Type,
            ci?.LifecycleState,
            ci?.SiteName,
            device.Address,
            device.PollerGroup,
            device.IsEnabled,
            device.Notes,
            checkCount,
            device.CreatedBy,
            device.CreatedAt,
            device.UpdatedBy,
            device.UpdatedAt);
    }

    internal static CheckResponse Map(CheckDefinition check) => new(
        check.Id,
        check.DeviceId,
        check.Type,
        check.Name,
        check.IntervalSeconds,
        check.TimeoutSeconds,
        check.WarningThreshold,
        check.CriticalThreshold,
        check.Comparison,
        Deserialize(check.ParametersJson),
        check.IsEnabled,
        check.CreatedBy,
        check.CreatedAt,
        check.UpdatedBy,
        check.UpdatedAt);

    internal static IReadOnlyDictionary<string, string> Deserialize(string parametersJson) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string[]> Field(string name, string message) =>
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [name] = [message] };

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
