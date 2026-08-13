using System.Security.Claims;

using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.Discovery;

public interface IScanProfileService
{
    Task<ScanProfilePageResponse> ListAsync(
        ScanProfileListRequest request,
        CancellationToken cancellationToken);

    Task<ScanProfileResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ScanProfileResult> CreateAsync(
        CreateScanProfileRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ScanProfileResult> UpdateAsync(
        Guid id,
        UpdateScanProfileRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<MonitoringOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    /// <summary>
    /// What one discovery group has to scan. The scanner's own read, and the only one behind
    /// <c>CanDiscover</c>: it returns enabled profiles in the named group and nothing else, so there is
    /// no request a scanner can make that widens the ranges it may probe.
    /// </summary>
    Task<DiscoveryConfigResponse> GetConfigAsync(string discoveryGroup, CancellationToken cancellationToken);
}
