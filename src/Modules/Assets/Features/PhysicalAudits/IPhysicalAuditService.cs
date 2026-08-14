using System.Security.Claims;

namespace Modules.Assets.Features.PhysicalAudits;

/// <summary>The physical half of reconciliation: walking a site with a scanner and reporting what did not turn up.</summary>
public interface IPhysicalAuditService
{
    Task<AuditSessionPageResponse> ListAsync(AuditSessionListRequest request, CancellationToken cancellationToken);

    Task<AuditSessionResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<AuditDiscrepancyReportResponse?> GetReportAsync(Guid id, CancellationToken cancellationToken);

    Task<AuditSessionResult> CreateAsync(
        CreateAuditSessionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<AuditSessionResult> RecordScanAsync(
        Guid sessionId,
        RecordAuditScanRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<AuditSessionResult> RemoveScanAsync(
        Guid sessionId,
        Guid scanId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<AuditSessionResult> CloseAsync(
        Guid sessionId,
        CloseAuditSessionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
