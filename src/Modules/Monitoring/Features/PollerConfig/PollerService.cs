using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Platform.Auditing;
using Platform.Integration;

namespace Modules.Monitoring.Features.PollerConfig;

public interface IPollerService
{
    Task<PollerListResponse> ListAsync(CancellationToken cancellationToken);

    Task<PollerResult> RegisterAsync(
        RegisterPollerRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<PollerConfigResult> GetConfigAsync(
        string pollerName,
        long? sinceVersion,
        CancellationToken cancellationToken);
}

public sealed class PollerService(
    MonitoringDbContext dbContext,
    ICiDirectory ciDirectory,
    IMonitoringConfigLog configLog,
    IAuditService auditService) : IPollerService
{
    public async Task<PollerListResponse> ListAsync(CancellationToken cancellationToken)
    {
        var currentVersion = await configLog.GetCurrentVersionAsync(cancellationToken);
        var pollers = await dbContext.Pollers.AsNoTracking()
            .OrderBy(poller => poller.Name)
            .ToListAsync(cancellationToken);
        return new([.. pollers.Select(poller => Map(poller, currentVersion))], currentVersion);
    }

    public async Task<PollerResult> RegisterAsync(
        RegisterPollerRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        var pollerGroup = string.IsNullOrWhiteSpace(request.PollerGroup)
            ? MonitoredDeviceService.DefaultPollerGroup
            : request.PollerGroup.Trim();
        var now = DateTimeOffset.UtcNow;

        // Registration is an upsert on the name: a poller that restarts is the same poller, not a
        // second one, and a redeploy that changes its group re-declares it here.
        var poller = await dbContext.Pollers.SingleOrDefaultAsync(item => item.Name == name, cancellationToken);
        var currentVersion = await configLog.GetCurrentVersionAsync(cancellationToken);
        var isNew = poller is null;
        var before = poller is null ? null : Map(poller, currentVersion);

        if (poller is null)
        {
            poller = new Poller
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                PollerGroup = pollerGroup,
                AgentVersion = request.AgentVersion?.Trim(),
                RegisteredAt = now,
                LastRegisteredAt = now,
            };
            dbContext.Pollers.Add(poller);
        }
        else
        {
            poller.PollerGroup = pollerGroup;
            poller.AgentVersion = request.AgentVersion?.Trim();
            poller.LastRegisteredAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(poller, currentVersion);
        await auditService.WriteAsync(
            actor, isNew ? "Registered" : "Reregistered", "Poller", poller.Id.ToString(),
            before, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<PollerConfigResult> GetConfigAsync(
        string pollerName,
        long? sinceVersion,
        CancellationToken cancellationToken)
    {
        var name = pollerName.Trim();
        var poller = await dbContext.Pollers.SingleOrDefaultAsync(item => item.Name == name, cancellationToken);
        if (poller is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var currentVersion = await configLog.GetCurrentVersionAsync(cancellationToken);
        if (sinceVersion is { } requested)
        {
            if (requested < 0)
            {
                return new(MonitoringOutcome.Invalid, Errors: Field(
                    nameof(sinceVersion), "sinceVersion cannot be negative."));
            }

            // A poller holding a version this server never issued is reading someone else's history —
            // a restored database, or a config pointed at the wrong environment. Refusing is the only
            // answer that does not quietly hand it a delta computed against the wrong past.
            if (requested > currentVersion)
            {
                return new(MonitoringOutcome.Invalid, Errors: Field(
                    nameof(sinceVersion),
                    $"sinceVersion {requested} is ahead of the current configuration version {currentVersion}."));
            }
        }

        var now = DateTimeOffset.UtcNow;
        var isFullSnapshot = sinceVersion is null or <= 0;

        var groupDevices = await dbContext.MonitoredDevices.AsNoTracking()
            .Where(device => device.PollerGroup == poller.PollerGroup && device.IsEnabled)
            .Select(device => device.Id)
            .ToListAsync(cancellationToken);
        var groupDeviceSet = groupDevices.ToHashSet();

        IReadOnlyList<Guid> deviceIdsToSend;
        IReadOnlyList<Guid> removedDeviceIds;
        if (isFullSnapshot)
        {
            deviceIdsToSend = groupDevices;
            removedDeviceIds = [];
        }
        else
        {
            var changes = await dbContext.ConfigChanges.AsNoTracking()
                .Where(change => change.Version > sinceVersion!.Value)
                .ToListAsync(cancellationToken);
            var delta = PollerConfigDeltaPlanner.Plan(poller.PollerGroup, changes, groupDeviceSet);
            deviceIdsToSend = delta.ChangedDeviceIds;
            removedDeviceIds = delta.RemovedDeviceIds;
        }

        var devices = await LoadDevicesAsync(deviceIdsToSend, cancellationToken);
        var windows = await LoadWindowsAsync(now, cancellationToken);

        // Recording what the poller now holds is bookkeeping rather than an operator action: it has
        // no before/after entity state to audit, and WP-3.2's heartbeat needs to know which pollers
        // are behind. It is deliberately the only write behind a GET in this module.
        poller.LastConfigVersion = currentVersion;
        poller.LastConfigFetchedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new(MonitoringOutcome.Success, new PollerConfigResponse(
            poller.Name,
            poller.PollerGroup,
            currentVersion,
            isFullSnapshot,
            devices,
            removedDeviceIds,
            windows,
            now));
    }

    private async Task<IReadOnlyList<PollerDeviceConfig>> LoadDevicesAsync(
        IReadOnlyList<Guid> deviceIds,
        CancellationToken cancellationToken)
    {
        if (deviceIds.Count == 0)
        {
            return [];
        }

        var devices = await dbContext.MonitoredDevices.AsNoTracking()
            .Where(device => deviceIds.Contains(device.Id))
            .Include(device => device.Checks)
            .OrderBy(device => device.Address).ThenBy(device => device.Id)
            .ToListAsync(cancellationToken);

        // The CI name travels with the config so a poller's logs name the device an operator knows,
        // without the poller ever reaching into the CMDB itself.
        var summaries = await ciDirectory.GetSummariesAsync(
            [.. devices.Select(device => device.CiId).Distinct()], cancellationToken);
        var namesByCiId = summaries.ToDictionary(summary => summary.Id, summary => summary.Name);

        return
        [
            .. devices.Select(device => new PollerDeviceConfig(
                device.Id,
                device.CiId,
                namesByCiId.GetValueOrDefault(device.CiId),
                device.Address,
                [
                    .. device.Checks.Where(check => check.IsEnabled)
                        .OrderBy(check => check.Name).ThenBy(check => check.Id)
                        .Select(check => new PollerCheckConfig(
                            check.Id,
                            check.Type,
                            check.Name,
                            check.IntervalSeconds,
                            check.TimeoutSeconds,
                            check.WarningThreshold,
                            check.CriticalThreshold,
                            check.Comparison,
                            MonitoredDeviceService.Deserialize(check.ParametersJson))),
                ])),
        ];
    }

    /// <summary>
    /// Active windows that have not already ended. A window in the past mutes nothing, and shipping
    /// the whole history to every poller on every cycle would grow without bound.
    /// </summary>
    private async Task<IReadOnlyList<PollerMaintenanceWindowConfig>> LoadWindowsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var windows = await dbContext.MaintenanceWindows.AsNoTracking()
            .Where(window => window.IsActive && window.EndsAt > now)
            .Include(window => window.Devices)
            .OrderBy(window => window.StartsAt).ThenBy(window => window.Id)
            .ToListAsync(cancellationToken);

        return
        [
            .. windows.Select(window => new PollerMaintenanceWindowConfig(
                window.Id,
                window.Name,
                window.StartsAt,
                window.EndsAt,
                window.AppliesToAllDevices,
                [.. window.Devices.Select(scope => scope.DeviceId).Order()])),
        ];
    }

    private static PollerResponse Map(Poller poller, long currentVersion) => new(
        poller.Id,
        poller.Name,
        poller.PollerGroup,
        poller.AgentVersion,
        poller.LastConfigVersion,
        poller.LastConfigFetchedAt,
        poller.RegisteredAt,
        poller.LastRegisteredAt,
        poller.IsEnabled,
        currentVersion);

    private static IReadOnlyDictionary<string, string[]> Field(string name, string message) =>
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [name] = [message] };
}
