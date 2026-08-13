using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Interfaces;

public interface IInterfaceService
{
    /// <summary>
    /// Every interface the last poll found on this device, in the device's own index order. Null —
    /// rather than empty — when there is no such device, so the endpoint can tell a switch nobody
    /// polls for interfaces from a device that does not exist.
    /// </summary>
    Task<IReadOnlyList<DeviceInterfaceResponse>?> ListAsync(Guid deviceId, CancellationToken cancellationToken);
}

public sealed class InterfaceService(MonitoringDbContext dbContext) : IInterfaceService
{
    public async Task<IReadOnlyList<DeviceInterfaceResponse>?> ListAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.MonitoredDevices.AnyAsync(device => device.Id == deviceId, cancellationToken))
        {
            return null;
        }

        // Unpaged, by the same reasoning as the check list: `MAX_INTERFACES` on the poller caps a
        // device at 256 rows, which is one screen of a table an operator scrolls rather than pages.
        var interfaces = await dbContext.DeviceInterfaces
            .AsNoTracking()
            .Where(link => link.DeviceId == deviceId)
            .OrderBy(link => link.IfIndex)
            .ToListAsync(cancellationToken);

        return [.. interfaces.Select(DeviceInterfaceResponse.From)];
    }
}
