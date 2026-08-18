using System.Security.Claims;

namespace Platform.Directory;

/// <summary>
/// The write surface over departments and locations, kept apart from <see cref="IDirectoryService"/>
/// so that interface stays the read-only surface modules depend on. Nothing outside Platform
/// implements this, and no module calls it — administering the organisation chart is Platform's own
/// job and reaches the API only through the AdminOnly endpoints.
/// </summary>
public interface IDirectoryAdminService
{
    Task<IReadOnlyList<DepartmentAdminResponse>> ListDepartmentsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SiteAdminResponse>> ListSitesAsync(CancellationToken cancellationToken);

    Task<DepartmentAdminResult> CreateDepartmentAsync(
        SaveDepartmentRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<DepartmentAdminResult> UpdateDepartmentAsync(
        Guid id,
        SaveDepartmentRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<DirectoryOutcome> DeleteDepartmentAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<SiteAdminResult> CreateSiteAsync(
        SaveSiteRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<SiteAdminResult> UpdateSiteAsync(
        Guid id,
        SaveSiteRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<DirectoryOutcome> DeleteSiteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);
}
