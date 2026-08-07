using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Platform.Integration;

namespace Modules.Assets.Features.Cis;

/// <summary>
/// Assets' implementation of the CI read port. Helpdesk renders linked-asset cards through this rather
/// than storing a name snapshot, so a renamed or retired CI reads correctly on every ticket at once.
/// </summary>
public sealed class CiDirectory(AssetsDbContext dbContext) : ICiDirectory
{
    public async Task<IReadOnlyList<CiSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.Distinct().ToList();
        var cis = await dbContext.Cis
            .Where(ci => wanted.Contains(ci.Id))
            .OrderBy(ci => ci.Name).ThenBy(ci => ci.Id)
            .ToListAsync(cancellationToken);
        return
        [
            .. cis.Select(ci => new CiSummary(
                ci.Id,
                ci.Type.ToString(),
                ci.Name,
                ci.AssetTag,
                ci.SerialNumber,
                ci.LifecycleState.ToString(),
                ci.IsActive,
                ci.OwnerName,
                ci.SiteName))
        ];
    }
}
