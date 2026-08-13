using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;

using Platform.Auditing;

namespace Modules.Assets.Features.Software;

/// <summary>
/// The catalogue behind software inventory: canonical products, the rules that map raw names onto them,
/// and the installs those rules resolve. Every write is audited, like every other write in this module.
/// </summary>
public sealed class SoftwareCatalogService(AssetsDbContext dbContext, IAuditService auditService)
    : ISoftwareCatalogService
{
    internal const int MaximumPageSize = 200;

    public async Task<SoftwareProductPageResponse> ListProductsAsync(
        SoftwareProductListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var query = dbContext.SoftwareProducts.AsNoTracking().AsQueryable();

        if (request.IsActive is { } isActive)
        {
            query = query.Where(product => product.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, term)
                || EF.Functions.ILike(product.Publisher, term)
                || (product.Category != null && EF.Functions.ILike(product.Category, term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var products = await query
            .OrderBy(product => product.Publisher).ThenBy(product => product.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(product => new
            {
                product.Id,
                product.Name,
                product.Publisher,
                product.Category,
                product.Notes,
                product.IsActive,
                RuleCount = product.Rules.Count,
                LicensePoolCount = product.LicensePools.Count,
                InstallCount = product.Installs.Count,
                product.CreatedAt,
                product.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return new(
            [
                .. products.Select(product => new SoftwareProductResponse(
                    product.Id, product.Name, product.Publisher, product.Category, product.Notes, product.IsActive,
                    product.RuleCount, product.LicensePoolCount, product.InstallCount,
                    product.CreatedAt, product.UpdatedAt))
            ],
            total,
            page,
            pageSize);
    }

    public async Task<SoftwareProductResponse?> GetProductAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.SoftwareProducts.AsNoTracking()
            .Where(product => product.Id == id)
            .Select(product => new SoftwareProductResponse(
                product.Id,
                product.Name,
                product.Publisher,
                product.Category,
                product.Notes,
                product.IsActive,
                product.Rules.Count,
                product.LicensePools.Count,
                product.Installs.Count,
                product.CreatedAt,
                product.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<SoftwareProductResult> CreateProductAsync(
        CreateSoftwareProductRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name.Trim();
        var publisher = request.Publisher.Trim();
        if (await ProductExistsAsync(publisher, name, null, cancellationToken))
        {
            return new(SoftwareOutcome.Duplicate, Error: $"'{publisher} {name}' is already in the catalogue.");
        }

        var now = DateTimeOffset.UtcNow;
        var product = new SoftwareProduct
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Publisher = publisher,
            Category = Trim(request.Category),
            Notes = Trim(request.Notes),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.SoftwareProducts.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", "SoftwareProduct", product.Id.ToString(), null, Snapshot(product), cancellationToken);
        return new(SoftwareOutcome.Success, Map(product, 0, 0, 0));
    }

    public async Task<SoftwareProductResult> UpdateProductAsync(
        Guid id,
        UpdateSoftwareProductRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var product = await dbContext.SoftwareProducts.SingleOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (product is null)
        {
            return new(SoftwareOutcome.NotFound);
        }

        var name = request.Name.Trim();
        var publisher = request.Publisher.Trim();
        if (await ProductExistsAsync(publisher, name, id, cancellationToken))
        {
            return new(SoftwareOutcome.Duplicate, Error: $"'{publisher} {name}' is already in the catalogue.");
        }

        var before = Snapshot(product);
        product.Name = name;
        product.Publisher = publisher;
        product.Category = Trim(request.Category);
        product.Notes = Trim(request.Notes);
        product.IsActive = request.IsActive;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Updated", "SoftwareProduct", id.ToString(), before, Snapshot(product), cancellationToken);
        return new(SoftwareOutcome.Success, await GetProductAsync(id, cancellationToken));
    }

    public async Task<SoftwareOutcome> DeleteProductAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var product = await dbContext.SoftwareProducts.SingleOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (product is null)
        {
            return SoftwareOutcome.NotFound;
        }

        // A product with history behind it is not deletable, following WP-1.9's ticket categories:
        // deleting it would silently un-normalise every install that points at it and orphan the pools
        // bought for it. Deactivation is the way out, and it keeps the history resolvable.
        var inUse = await dbContext.InstalledSoftware.AnyAsync(install => install.ProductId == id, cancellationToken)
            || await dbContext.LicensePools.AnyAsync(pool => pool.ProductId == id, cancellationToken)
            || await dbContext.SoftwareNormalisationRules.AnyAsync(rule => rule.ProductId == id, cancellationToken);
        if (inUse)
        {
            return SoftwareOutcome.InUse;
        }

        var before = Snapshot(product);
        dbContext.SoftwareProducts.Remove(product);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "SoftwareProduct", id.ToString(), before, null, cancellationToken);
        return SoftwareOutcome.Success;
    }

    public async Task<IReadOnlyList<SoftwareRuleResponse>> ListRulesAsync(
        Guid? productId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SoftwareNormalisationRules.AsNoTracking().Include(rule => rule.Product).AsQueryable();
        if (productId is { } id)
        {
            query = query.Where(rule => rule.ProductId == id);
        }

        var rules = await query.ToListAsync(cancellationToken);

        // Listed in the order the matcher walks them, so the page reads as the decision procedure it is.
        var order = SoftwareNormaliser.Ordered(
                [.. rules.Select(rule => new SoftwareRule(rule.ProductId, rule.MatchKind, rule.Pattern, rule.Priority))])
            .Select((rule, index) => (Key: (rule.MatchKind, rule.Pattern), Index: index))
            .ToDictionary(entry => entry.Key, entry => entry.Index);
        return
        [
            .. rules
                .OrderBy(rule => order.TryGetValue((rule.MatchKind, rule.Pattern), out var index) ? index : int.MaxValue)
                .Select(Map)
        ];
    }

    public async Task<SoftwareRuleResult> CreateRuleAsync(
        CreateSoftwareRuleRequest request,
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

        var pattern = SoftwareNormaliser.Canonicalise(request.Pattern);
        if (await RuleExistsAsync(request.MatchKind, pattern, null, cancellationToken))
        {
            return new(
                SoftwareOutcome.Duplicate,
                Error: $"A {request.MatchKind} rule for '{pattern}' already exists; one pattern cannot mean two products.");
        }

        var now = DateTimeOffset.UtcNow;
        var rule = new SoftwareNormalisationRule
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            Product = product,
            MatchKind = request.MatchKind,
            Pattern = pattern,
            Priority = request.Priority,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.SoftwareNormalisationRules.Add(rule);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", "SoftwareNormalisationRule", rule.Id.ToString(), null, Snapshot(rule), cancellationToken);
        return new(SoftwareOutcome.Success, Map(rule));
    }

    public async Task<SoftwareRuleResult> UpdateRuleAsync(
        Guid id,
        UpdateSoftwareRuleRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rule = await dbContext.SoftwareNormalisationRules.Include(item => item.Product)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (rule is null)
        {
            return new(SoftwareOutcome.NotFound);
        }

        var product = await dbContext.SoftwareProducts.SingleOrDefaultAsync(
            item => item.Id == request.ProductId, cancellationToken);
        if (product is null)
        {
            return new(SoftwareOutcome.NotFound, Error: "No product with that id is in the catalogue.");
        }

        var pattern = SoftwareNormaliser.Canonicalise(request.Pattern);
        if (await RuleExistsAsync(request.MatchKind, pattern, id, cancellationToken))
        {
            return new(
                SoftwareOutcome.Duplicate,
                Error: $"A {request.MatchKind} rule for '{pattern}' already exists; one pattern cannot mean two products.");
        }

        var before = Snapshot(rule);
        rule.ProductId = product.Id;
        rule.Product = product;
        rule.MatchKind = request.MatchKind;
        rule.Pattern = pattern;
        rule.Priority = request.Priority;
        rule.IsActive = request.IsActive;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Updated", "SoftwareNormalisationRule", id.ToString(), before, Snapshot(rule), cancellationToken);
        return new(SoftwareOutcome.Success, Map(rule));
    }

    public async Task<SoftwareOutcome> DeleteRuleAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var rule = await dbContext.SoftwareNormalisationRules.SingleOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (rule is null)
        {
            return SoftwareOutcome.NotFound;
        }

        // Deleting a rule is not deleting what it decided: the installs it normalised keep their
        // product until somebody re-normalises, which is the pass that makes the change visible.
        var before = Snapshot(rule);
        dbContext.SoftwareNormalisationRules.Remove(rule);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "SoftwareNormalisationRule", id.ToString(), before, null, cancellationToken);
        return SoftwareOutcome.Success;
    }

    public async Task<SoftwareNormalisationRunResponse> NormaliseAsync(
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var rules = await LoadRulesAsync(cancellationToken);
        var installs = await dbContext.InstalledSoftware.ToListAsync(cancellationToken);
        var normalised = 0;
        var renormalised = 0;
        foreach (var install in installs)
        {
            var matched = SoftwareNormaliser.Match(install.RawName, rules);
            if (matched == install.ProductId)
            {
                continue;
            }

            if (install.ProductId is null)
            {
                normalised++;
            }
            else
            {
                // Includes the rule that was withdrawn: a name that no longer resolves goes back to
                // unrecognised rather than keeping a product nothing claims it belongs to.
                renormalised++;
            }

            install.ProductId = matched;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var unrecognised = installs.Count(install => install.ProductId is null);
        await auditService.WriteAsync(
            actor,
            "Normalised",
            "SoftwareCatalog",
            "catalog",
            null,
            new { Examined = installs.Count, Normalised = normalised, Renormalised = renormalised, Unrecognised = unrecognised },
            cancellationToken);
        return new(installs.Count, normalised, renormalised, unrecognised);
    }

    public async Task<InstalledSoftwarePageResponse> ListInstallsAsync(
        InstalledSoftwareListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var query = dbContext.InstalledSoftware.AsNoTracking().AsQueryable();

        if (request.CiId is { } ciId)
        {
            query = query.Where(install => install.CiId == ciId);
        }

        if (request.ProductId is { } productId)
        {
            query = query.Where(install => install.ProductId == productId);
        }

        if (request.IsNormalised is { } isNormalised)
        {
            query = isNormalised
                ? query.Where(install => install.ProductId != null)
                : query.Where(install => install.ProductId == null);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(install =>
                EF.Functions.ILike(install.RawName, term)
                || (install.RawPublisher != null && EF.Functions.ILike(install.RawPublisher, term))
                || (install.Version != null && EF.Functions.ILike(install.Version, term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(install => install.RawName).ThenBy(install => install.Version).ThenBy(install => install.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(install => new
            {
                install.Id,
                install.CiId,
                CiName = install.Ci.Name,
                install.RawName,
                install.RawPublisher,
                install.Version,
                install.ProductId,
                ProductName = install.Product == null ? null : install.Product.Name,
                ProductPublisher = install.Product == null ? null : install.Product.Publisher,
                install.InstalledOn,
                install.Source,
                install.FirstSeenAt,
                install.LastSeenAt,
                install.SightingCount,
            })
            .ToListAsync(cancellationToken);

        return new(
            [
                .. rows.Select(row => new InstalledSoftwareResponse(
                    row.Id, row.CiId, row.CiName, row.RawName, row.RawPublisher, row.Version, row.ProductId,
                    row.ProductName, row.ProductPublisher, row.InstalledOn, row.Source, row.FirstSeenAt,
                    row.LastSeenAt, row.SightingCount))
            ],
            total,
            page,
            pageSize);
    }

    public async Task<IReadOnlyList<UnrecognisedSoftwareResponse>> ListUnrecognisedAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.InstalledSoftware.AsNoTracking()
            .Where(install => install.ProductId == null)
            .GroupBy(install => install.RawName)
            .Select(group => new
            {
                RawName = group.Key,
                RawPublisher = group.Max(install => install.RawPublisher),
                InstallCount = group.Count(),
                CiCount = group.Select(install => install.CiId).Distinct().Count(),
            })
            .OrderByDescending(row => row.CiCount).ThenBy(row => row.RawName)
            .Take(Math.Clamp(limit, 1, MaximumPageSize))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new UnrecognisedSoftwareResponse(
                row.RawName, row.RawPublisher, row.InstallCount, row.CiCount))
        ];
    }

    /// <summary>
    /// The active catalogue, flattened for the matcher. Static because the import runs the same query
    /// and one definition of "which rules count" is what keeps a preview and a re-normalisation
    /// agreeing — a rule on a deactivated product is not in force.
    /// </summary>
    internal static async Task<IReadOnlyList<SoftwareRule>> ActiveRulesAsync(
        AssetsDbContext dbContext,
        CancellationToken cancellationToken) =>
        await dbContext.SoftwareNormalisationRules.AsNoTracking()
            .Where(rule => rule.IsActive && rule.Product.IsActive)
            .Select(rule => new SoftwareRule(rule.ProductId, rule.MatchKind, rule.Pattern, rule.Priority))
            .ToListAsync(cancellationToken);

    private Task<IReadOnlyList<SoftwareRule>> LoadRulesAsync(CancellationToken cancellationToken) =>
        ActiveRulesAsync(dbContext, cancellationToken);

    private Task<bool> ProductExistsAsync(
        string publisher,
        string name,
        Guid? excluding,
        CancellationToken cancellationToken) =>
        dbContext.SoftwareProducts.AnyAsync(
            product => product.Publisher.ToLower() == publisher.ToLower()
                && product.Name.ToLower() == name.ToLower()
                && (excluding == null || product.Id != excluding),
            cancellationToken);

    private Task<bool> RuleExistsAsync(
        SoftwareMatchKind matchKind,
        string pattern,
        Guid? excluding,
        CancellationToken cancellationToken) =>
        dbContext.SoftwareNormalisationRules.AnyAsync(
            rule => rule.MatchKind == matchKind && rule.Pattern == pattern
                && (excluding == null || rule.Id != excluding),
            cancellationToken);

    private static SoftwareProductResponse Map(
        SoftwareProduct product,
        int ruleCount,
        int poolCount,
        int installCount) => new(
        product.Id,
        product.Name,
        product.Publisher,
        product.Category,
        product.Notes,
        product.IsActive,
        ruleCount,
        poolCount,
        installCount,
        product.CreatedAt,
        product.UpdatedAt);

    private static SoftwareRuleResponse Map(SoftwareNormalisationRule rule) => new(
        rule.Id,
        rule.ProductId,
        rule.Product.Name,
        rule.Product.Publisher,
        rule.MatchKind,
        rule.Pattern,
        rule.Priority,
        rule.IsActive,
        rule.CreatedAt,
        rule.UpdatedAt);

    private static object Snapshot(SoftwareProduct product) => new
    {
        product.Name,
        product.Publisher,
        product.Category,
        product.Notes,
        product.IsActive,
    };

    private static object Snapshot(SoftwareNormalisationRule rule) => new
    {
        rule.ProductId,
        MatchKind = rule.MatchKind.ToString(),
        rule.Pattern,
        rule.Priority,
        rule.IsActive,
    };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
