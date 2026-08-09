using System.Security.Claims;

namespace Modules.Monitoring.Features.Devices;

public interface IMonitoredDeviceService
{
    Task<MonitoredDevicePageResponse> ListAsync(
        MonitoredDeviceListRequest request,
        CancellationToken cancellationToken);

    Task<MonitoredDeviceResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<MonitoredDeviceResult> CreateAsync(
        CreateMonitoredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<MonitoredDeviceResult> UpdateAsync(
        Guid id,
        UpdateMonitoredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<MonitoringOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<CheckResponse>?> ListChecksAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<CheckResult> CreateCheckAsync(
        Guid deviceId,
        CreateCheckRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CheckResult> UpdateCheckAsync(
        Guid checkId,
        UpdateCheckRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<MonitoringOutcome> DeleteCheckAsync(
        Guid checkId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
