using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Platform.Directory;

namespace Modules.Assets.Features.Lifecycle;

/// <summary>
/// Assets' answer to whether a department or location may be deleted. Configuration items carry a
/// department and a site as plain ids — a foreign key into the <c>platform</c> schema would be the
/// module-boundary violation ARCHITECTURE §3 forbids — so this is what keeps a delete from stranding
/// them.
/// </summary>
public sealed class CiDirectoryUsageSource(AssetsDbContext dbContext) : IDirectoryUsageSource
{
    public string ResourceName => "configuration items";

    public Task<int> CountByDepartmentAsync(Guid departmentId, CancellationToken cancellationToken) =>
        dbContext.Cis.CountAsync(ci => ci.DepartmentId == departmentId, cancellationToken);

    public Task<int> CountBySiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        dbContext.Cis.CountAsync(ci => ci.SiteId == siteId, cancellationToken);
}
