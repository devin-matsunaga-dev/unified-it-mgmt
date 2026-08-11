using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;
using Platform.Integration;

namespace Modules.Assets.Features.Cis;

/// <summary>
/// Assets' implementation of the CI read port. Helpdesk renders linked-asset cards through this rather
/// than storing a name snapshot, so a renamed or retired CI reads correctly on every ticket at once —
/// and WP-3.7 reads the same way for an alert's CMDB context.
/// <para>
/// Warranty status is computed here rather than by the caller, through the same
/// <see cref="ContractExpiryCalculator"/> the renewal job and the contract screens use, so "expiring
/// soon" means the same thirty days on a ticket, on an alert and in a notice.
/// </para>
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
            .Include(ci => ci.Contract)
            .Where(ci => wanted.Contains(ci.Id))
            .OrderBy(ci => ci.Name).ThenBy(ci => ci.Id)
            .ToListAsync(cancellationToken);
        var today = ContractExpiryCalculator.Today();
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
                ci.SiteName,
                ci.DepartmentName,
                ci.WarrantyExpiresAt,
                ci.WarrantyExpiresAt is { } expiry
                    ? ContractExpiryCalculator.Status(expiry, today).ToString()
                    : null,
                ci.WarrantyExpiresAt is { } due
                    ? ContractExpiryCalculator.DaysRemaining(due, today)
                    : null,
                ci.Contract?.Name))
        ];
    }
}
