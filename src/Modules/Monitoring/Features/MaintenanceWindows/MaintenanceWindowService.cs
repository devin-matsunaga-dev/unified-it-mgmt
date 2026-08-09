using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Modules.Monitoring.Features.PollerConfig;
using Platform.Auditing;

namespace Modules.Monitoring.Features.MaintenanceWindows;

public interface IMaintenanceWindowService
{
    Task<MaintenanceWindowPageResponse> ListAsync(
        MaintenanceWindowListRequest request,
        CancellationToken cancellationToken);

    Task<MaintenanceWindowResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<MaintenanceWindowResult> CreateAsync(
        CreateMaintenanceWindowRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<MaintenanceWindowResult> UpdateAsync(
        Guid id,
        UpdateMaintenanceWindowRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<MonitoringOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);
}

public sealed class MaintenanceWindowService(
    MonitoringDbContext dbContext,
    IMonitoringConfigLog configLog,
    IAuditService auditService) : IMaintenanceWindowService
{
    private const int MaximumPageSize = 200;
    private const int MaximumScopedDevices = 500;

    public async Task<MaintenanceWindowPageResponse> ListAsync(
        MaintenanceWindowListRequest request,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var now = DateTimeOffset.UtcNow;

        var query = dbContext.MaintenanceWindows.AsNoTracking().Include(window => window.Devices).AsQueryable();
        if (request.IsActive is { } isActive)
        {
            query = query.Where(window => window.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(window => EF.Functions.ILike(window.Name, term));
        }

        // An estate-wide window covers the named device too, so it belongs in the answer.
        if (request.DeviceId is { } deviceId)
        {
            query = query.Where(window => window.AppliesToAllDevices
                || window.Devices.Any(scope => scope.DeviceId == deviceId));
        }

        query = request.Status switch
        {
            MaintenanceWindowStatus.Scheduled => query.Where(window => window.StartsAt > now),
            MaintenanceWindowStatus.InProgress =>
                query.Where(window => window.StartsAt <= now && window.EndsAt > now),
            MaintenanceWindowStatus.Ended => query.Where(window => window.EndsAt <= now),
            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);
        var windows = await query
            .OrderByDescending(window => window.StartsAt).ThenBy(window => window.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new([.. windows.Select(window => Map(window, now))], total, page, pageSize);
    }

    public async Task<MaintenanceWindowResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var window = await dbContext.MaintenanceWindows.AsNoTracking()
            .Include(item => item.Devices)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return window is null ? null : Map(window, DateTimeOffset.UtcNow);
    }

    public async Task<MaintenanceWindowResult> CreateAsync(
        CreateMaintenanceWindowRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var deviceIds = Normalise(request.DeviceIds);
        if (await ValidateAsync(request.StartsAt, request.EndsAt, request.AppliesToAllDevices, deviceIds,
                cancellationToken) is { Count: > 0 } errors)
        {
            return new(MonitoringOutcome.Invalid, Errors: errors);
        }

        var actorId = GetActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var window = new MaintenanceWindow
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            AppliesToAllDevices = request.AppliesToAllDevices,
            IsActive = request.IsActive,
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
            Devices = request.AppliesToAllDevices
                ? []
                : [.. deviceIds.Select(deviceId => new MaintenanceWindowDevice { DeviceId = deviceId })],
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.MaintenanceWindows.Add(window);
        await configLog.RecordAsync(
            MonitoringConfigEntity.MaintenanceWindow, window.Id, deviceId: null, pollerGroup: null,
            MonitoringConfigChangeKind.Upserted, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(window, now);
        await auditService.WriteAsync(
            actor, "Created", "MaintenanceWindow", window.Id.ToString(), null, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<MaintenanceWindowResult> UpdateAsync(
        Guid id,
        UpdateMaintenanceWindowRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var window = await dbContext.MaintenanceWindows
            .Include(item => item.Devices)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (window is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var deviceIds = Normalise(request.DeviceIds);
        if (await ValidateAsync(request.StartsAt, request.EndsAt, request.AppliesToAllDevices, deviceIds,
                cancellationToken) is { Count: > 0 } errors)
        {
            return new(MonitoringOutcome.Invalid, Errors: errors);
        }

        var now = DateTimeOffset.UtcNow;
        var before = Map(window, now);

        window.Name = request.Name.Trim();
        window.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        window.StartsAt = request.StartsAt;
        window.EndsAt = request.EndsAt;
        window.AppliesToAllDevices = request.AppliesToAllDevices;
        window.IsActive = request.IsActive;
        window.UpdatedBy = GetActorId(actor);
        window.UpdatedAt = now;

        // The scope is a complete statement, following WP-2.2's assignment endpoint: what is not in
        // the payload is not in the window.
        window.Devices.Clear();
        if (!request.AppliesToAllDevices)
        {
            foreach (var deviceId in deviceIds)
            {
                window.Devices.Add(new MaintenanceWindowDevice { MaintenanceWindowId = window.Id, DeviceId = deviceId });
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await configLog.RecordAsync(
            MonitoringConfigEntity.MaintenanceWindow, window.Id, deviceId: null, pollerGroup: null,
            MonitoringConfigChangeKind.Upserted, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(window, now);
        await auditService.WriteAsync(
            actor, "Updated", "MaintenanceWindow", window.Id.ToString(), before, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<MonitoringOutcome> DeleteAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var window = await dbContext.MaintenanceWindows
            .Include(item => item.Devices)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (window is null)
        {
            return MonitoringOutcome.NotFound;
        }

        var before = Map(window, DateTimeOffset.UtcNow);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.MaintenanceWindows.Remove(window);
        await configLog.RecordAsync(
            MonitoringConfigEntity.MaintenanceWindow, id, deviceId: null, pollerGroup: null,
            MonitoringConfigChangeKind.Removed, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "MaintenanceWindow", id.ToString(), before, null, cancellationToken);
        return MonitoringOutcome.Success;
    }

    private async Task<IReadOnlyDictionary<string, string[]>> ValidateAsync(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        bool appliesToAllDevices,
        IReadOnlyList<Guid> deviceIds,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (endsAt <= startsAt)
        {
            errors["EndsAt"] = ["A maintenance window must end after it starts."];
        }

        if (appliesToAllDevices)
        {
            // Silently ignoring the list would leave the caller believing they had scoped the window.
            if (deviceIds.Count > 0)
            {
                errors["DeviceIds"] =
                    ["An estate-wide window covers every device; remove the device list or unset appliesToAllDevices."];
            }

            return errors;
        }

        if (deviceIds.Count == 0)
        {
            errors["DeviceIds"] = ["Name the devices this window covers, or set appliesToAllDevices."];
            return errors;
        }

        if (deviceIds.Count > MaximumScopedDevices)
        {
            errors["DeviceIds"] = [$"A window covers at most {MaximumScopedDevices} named devices."];
            return errors;
        }

        var known = await dbContext.MonitoredDevices
            .Where(device => deviceIds.Contains(device.Id))
            .Select(device => device.Id)
            .ToListAsync(cancellationToken);
        if (known.Count != deviceIds.Count)
        {
            var missing = deviceIds.Except(known).Select(id => id.ToString());
            errors["DeviceIds"] = [$"Unknown monitored devices: {string.Join(", ", missing)}."];
        }

        return errors;
    }

    private static IReadOnlyList<Guid> Normalise(IReadOnlyList<Guid>? deviceIds) =>
        deviceIds is null ? [] : [.. deviceIds.Distinct()];

    internal static MaintenanceWindowStatus StatusAt(MaintenanceWindow window, DateTimeOffset now) =>
        now < window.StartsAt ? MaintenanceWindowStatus.Scheduled
        : now < window.EndsAt ? MaintenanceWindowStatus.InProgress
        : MaintenanceWindowStatus.Ended;

    private static MaintenanceWindowResponse Map(MaintenanceWindow window, DateTimeOffset now) => new(
        window.Id,
        window.Name,
        window.Description,
        window.StartsAt,
        window.EndsAt,
        window.AppliesToAllDevices,
        [.. window.Devices.Select(scope => scope.DeviceId).Order()],
        window.IsActive,
        StatusAt(window, now),
        window.CreatedBy,
        window.CreatedAt,
        window.UpdatedBy,
        window.UpdatedAt);

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
