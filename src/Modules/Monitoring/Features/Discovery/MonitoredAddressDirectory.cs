using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Platform.Integration;

namespace Modules.Monitoring.Features.Discovery;

/// <summary>
/// The Monitoring half of WP-4.2's match ladder: which CI, if any, is already polled at an address.
/// <para>
/// Disabled devices count. A device somebody switched off is still that operator's statement that this
/// address is that CI, and treating it as unknown would queue a device the estate plainly knows —
/// which is the same reasoning <see cref="Platform.Integration.ICredentialUsageDirectory"/> uses for
/// counting disabled checks.
/// </para>
/// </summary>
public sealed class MonitoredAddressDirectory(MonitoringDbContext dbContext) : IMonitoredAddressDirectory
{
    public async Task<Guid?> FindCiByAddressAsync(
        IReadOnlyCollection<string> addresses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var candidates = addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var ciIds = await dbContext.MonitoredDevices.AsNoTracking()
            .Where(device => candidates.Contains(device.Address.ToLower()))
            .Select(device => device.CiId)
            .Distinct()
            .Take(2)
            .ToListAsync(cancellationToken);

        // Two CIs monitored at addresses this one discovery answers to is a contradiction in the
        // estate, not a tie to break here. Answering null drops the discovery to the next rung and,
        // failing that, onto somebody's review queue where the two can be looked at together.
        return ciIds.Count == 1 ? ciIds[0] : null;
    }
}
