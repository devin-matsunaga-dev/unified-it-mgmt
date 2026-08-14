namespace Modules.Assets.Features.Drift;

/// <summary>Where the CMDB and the network disagree, computed on every read and stored nowhere.</summary>
public interface IDriftService
{
    Task<DriftReportResponse> GetAsync(DriftReportRequest request, CancellationToken cancellationToken);
}
