using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;

using Platform.Auditing;

namespace Modules.Assets.Features.Software;

/// <summary>
/// Licence pools, and the installed-versus-entitled report they exist to be read through.
/// <para>
/// Compliance is computed per <em>product</em> rather than per pool: licences are bought in blocks over
/// time, so a product with three pools has one install count and one entitlement total, and asking a
/// single pool whether it is compliant has no answer.
/// </para>
/// </summary>
public sealed class LicensingService(AssetsDbContext dbContext, IAuditService auditService) : ILicensingService
{
    internal const int MaximumPageSize = 200;

    public async Task<LicensePoolPageResponse> ListPoolsAsync(
        LicensePoolListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var today = ContractExpiryCalculator.Today();
        var query = dbContext.LicensePools.AsNoTracking().Include(pool => pool.Product).AsQueryable();

        if (request.ProductId is { } productId)
        {
            query = query.Where(pool => pool.ProductId == productId);
        }

        if (request.IsActive is { } isActive)
        {
            query = query.Where(pool => pool.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(pool =>
                EF.Functions.ILike(pool.Name, term)
                || (pool.Reference != null && EF.Functions.ILike(pool.Reference, term))
                || EF.Functions.ILike(pool.Product.Name, term)
                || EF.Functions.ILike(pool.Product.Publisher, term));
        }

        // A perpetual licence has no end date and therefore no status, so it belongs to none of the
        // three filters rather than quietly answering "Active".
        query = request.Status switch
        {
            ContractExpiryStatus.Expired => query.Where(pool => pool.ExpiresAt != null && pool.ExpiresAt < today),
            ContractExpiryStatus.ExpiringSoon => query.Where(pool =>
                pool.ExpiresAt != null && pool.ExpiresAt >= today && pool.ExpiresAt <= today.AddDays(30)),
            ContractExpiryStatus.Active => query.Where(pool => pool.ExpiresAt != null && pool.ExpiresAt > today.AddDays(30)),
            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);
        var pools = await query
            .OrderBy(pool => pool.Product.Publisher).ThenBy(pool => pool.Product.Name).ThenBy(pool => pool.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new([.. pools.Select(pool => Map(pool, today))], total, page, pageSize);
    }

    public async Task<LicensePoolResponse?> GetPoolAsync(Guid id, CancellationToken cancellationToken)
    {
        var pool = await dbContext.LicensePools.AsNoTracking().Include(item => item.Product)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return pool is null ? null : Map(pool, ContractExpiryCalculator.Today());
    }

    public async Task<LicensePoolResult> CreatePoolAsync(
        CreateLicensePoolRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var product = await dbContext.SoftwareProducts.SingleOrDefaultAsync(
            item => item.Id == request.ProductId, cancellationToken);
        if (product is null)
        {
            return new(SoftwareOutcome.NotFound, Error: "No product with that id is in the catalogue.");
        }

        var name = request.Name.Trim();
        if (await PoolExistsAsync(product.Id, name, null, cancellationToken))
        {
            return new(SoftwareOutcome.Duplicate, Error: $"'{product.Name}' already has a pool named '{name}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var pool = new LicensePool
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            Product = product,
            Name = name,
            Reference = Trim(request.Reference),
            Entitlements = request.Entitlements,
            PurchaseDate = request.PurchaseDate,
            ExpiresAt = request.ExpiresAt,
            Notes = Trim(request.Notes),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.LicensePools.Add(pool);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", "LicensePool", pool.Id.ToString(), null, Snapshot(pool), cancellationToken);
        return new(SoftwareOutcome.Success, Map(pool, ContractExpiryCalculator.Today()));
    }

    public async Task<LicensePoolResult> UpdatePoolAsync(
        Guid id,
        UpdateLicensePoolRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pool = await dbContext.LicensePools.Include(item => item.Product)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (pool is null)
        {
            return new(SoftwareOutcome.NotFound);
        }

        var product = await dbContext.SoftwareProducts.SingleOrDefaultAsync(
            item => item.Id == request.ProductId, cancellationToken);
        if (product is null)
        {
            return new(SoftwareOutcome.NotFound, Error: "No product with that id is in the catalogue.");
        }

        var name = request.Name.Trim();
        if (await PoolExistsAsync(product.Id, name, id, cancellationToken))
        {
            return new(SoftwareOutcome.Duplicate, Error: $"'{product.Name}' already has a pool named '{name}'.");
        }

        var before = Snapshot(pool);
        pool.ProductId = product.Id;
        pool.Product = product;
        pool.Name = name;
        pool.Reference = Trim(request.Reference);
        pool.Entitlements = request.Entitlements;
        pool.PurchaseDate = request.PurchaseDate;
        pool.ExpiresAt = request.ExpiresAt;
        pool.Notes = Trim(request.Notes);
        pool.IsActive = request.IsActive;
        pool.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Updated", "LicensePool", id.ToString(), before, Snapshot(pool), cancellationToken);
        return new(SoftwareOutcome.Success, Map(pool, ContractExpiryCalculator.Today()));
    }

    public async Task<SoftwareOutcome> DeletePoolAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var pool = await dbContext.LicensePools.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (pool is null)
        {
            return SoftwareOutcome.NotFound;
        }

        var before = Snapshot(pool);
        dbContext.LicensePools.Remove(pool);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Deleted", "LicensePool", id.ToString(), before, null, cancellationToken);
        return SoftwareOutcome.Success;
    }

    public async Task<SoftwareComplianceResponse> ReportAsync(
        SoftwareComplianceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rows = await TallyAsync(ContractExpiryCalculator.Today(), cancellationToken);

        var filtered = rows.AsEnumerable();
        if (request.State is { } state)
        {
            filtered = filtered.Where(row => row.State == state);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            filtered = filtered.Where(row =>
                row.ProductName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Publisher.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // The totals describe the estate, not the filter: a page showing only the over-deployed rows
        // still says how many products there are to be over-deployed out of.
        return new(
            ContractExpiryCalculator.Today(),
            rows.Count,
            rows.Count(row => row.State == SoftwareComplianceState.OverDeployed),
            rows.Count(row => row.State == SoftwareComplianceState.Unlicensed),
            rows.Sum(row => row.InstallCount),
            rows.Sum(row => row.Entitled),
            [.. filtered]);
    }

    /// <summary>
    /// The report itself: one row per product that anything references, computed in three grouped
    /// queries rather than by walking products and counting each one.
    /// </summary>
    internal async Task<IReadOnlyList<SoftwareComplianceRowResponse>> TallyAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var installs = await dbContext.InstalledSoftware.AsNoTracking()
            .Where(install => install.ProductId != null)
            .GroupBy(install => install.ProductId!.Value)
            .Select(group => new
            {
                ProductId = group.Key,
                InstallCount = group.Count(),
                CiCount = group.Select(install => install.CiId).Distinct().Count(),
            })
            .ToDictionaryAsync(row => row.ProductId, cancellationToken);

        var pools = await dbContext.LicensePools.AsNoTracking()
            .Select(pool => new
            {
                pool.ProductId,
                pool.Entitlements,
                pool.IsActive,
                pool.ExpiresAt,
            })
            .ToListAsync(cancellationToken);
        var poolsByProduct = pools.GroupBy(pool => pool.ProductId).ToDictionary(group => group.Key, group => group.ToList());

        var wanted = installs.Keys.Concat(poolsByProduct.Keys).Distinct().ToArray();
        var products = await dbContext.SoftwareProducts.AsNoTracking()
            .Where(product => wanted.Contains(product.Id))
            .Select(product => new { product.Id, product.Name, product.Publisher, product.Category })
            .ToListAsync(cancellationToken);

        var rows = new List<SoftwareComplianceRowResponse>(products.Count);
        foreach (var product in products)
        {
            var install = installs.GetValueOrDefault(product.Id);
            var productPools = poolsByProduct.GetValueOrDefault(product.Id) ?? [];
            var live = productPools
                .Where(pool => SoftwareComplianceCalculator.IsLive(pool.IsActive, pool.ExpiresAt, today))
                .ToList();
            var tally = new SoftwareComplianceTally(
                install?.CiCount ?? 0,
                productPools.Count,
                live.Count,
                live.Sum(pool => pool.Entitlements));

            // The pool that lapses first is the one worth naming: it is the date on which this row's
            // entitlement drops and its compliance can change.
            var nextExpiry = live.Select(pool => pool.ExpiresAt).OfType<DateOnly>().Cast<DateOnly?>().Min();
            rows.Add(new(
                product.Id,
                product.Name,
                product.Publisher,
                product.Category,
                tally.InstalledCiCount,
                install?.InstallCount ?? 0,
                tally.Entitled,
                productPools.Count,
                productPools.Count(pool => pool.ExpiresAt is { } expiry && expiry < today),
                tally.Overage,
                SoftwareComplianceCalculator.State(tally),
                nextExpiry,
                SoftwareComplianceCalculator.Status(nextExpiry, today)));
        }

        // Worst first: the rows somebody has to act on are the ones with the biggest shortfall.
        return
        [
            .. rows
                .OrderByDescending(row => row.State == SoftwareComplianceState.OverDeployed)
                .ThenByDescending(row => row.Overage)
                .ThenBy(row => row.Publisher)
                .ThenBy(row => row.ProductName)
        ];
    }

    private Task<bool> PoolExistsAsync(
        Guid productId,
        string name,
        Guid? excluding,
        CancellationToken cancellationToken) =>
        dbContext.LicensePools.AnyAsync(
            pool => pool.ProductId == productId && pool.Name.ToLower() == name.ToLower()
                && (excluding == null || pool.Id != excluding),
            cancellationToken);

    private static LicensePoolResponse Map(LicensePool pool, DateOnly today) => new(
        pool.Id,
        pool.ProductId,
        pool.Product.Name,
        pool.Product.Publisher,
        pool.Name,
        pool.Reference,
        pool.Entitlements,
        pool.PurchaseDate,
        pool.ExpiresAt,
        pool.Notes,
        pool.IsActive,
        SoftwareComplianceCalculator.Status(pool.ExpiresAt, today),
        pool.ExpiresAt is { } expiry ? ContractExpiryCalculator.DaysRemaining(expiry, today) : null,
        pool.CreatedAt,
        pool.UpdatedAt);

    private static object Snapshot(LicensePool pool) => new
    {
        pool.ProductId,
        pool.Name,
        pool.Reference,
        pool.Entitlements,
        pool.PurchaseDate,
        pool.ExpiresAt,
        pool.IsActive,
    };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
