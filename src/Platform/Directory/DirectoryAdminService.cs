using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Platform.Auditing;
using Platform.Data;

namespace Platform.Directory;

public sealed class DirectoryAdminService(
    PlatformDbContext dbContext,
    IAuditService auditService,
    IEnumerable<IDirectoryUsageSource> usageSources) : IDirectoryAdminService
{
    public async Task<IReadOnlyList<DepartmentAdminResponse>> ListDepartmentsAsync(CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .Include(department => department.Sites).ThenInclude(link => link.Site)
            .OrderBy(department => department.Name)
            .ToListAsync(cancellationToken);
        var userCounts = await UserCountsByDepartmentAsync(cancellationToken);
        return [.. departments.Select(department => MapDepartment(department, userCounts))];
    }

    public async Task<IReadOnlyList<SiteAdminResponse>> ListSitesAsync(CancellationToken cancellationToken)
    {
        var sites = await dbContext.Sites
            .Include(site => site.Departments).ThenInclude(link => link.Department)
            .OrderBy(site => site.Name)
            .ToListAsync(cancellationToken);
        var userCounts = await dbContext.UserProfiles
            .GroupBy(user => user.SiteId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);
        return [.. sites.Select(site => MapSite(site, userCounts))];
    }

    public async Task<DepartmentAdminResult> CreateDepartmentAsync(
        SaveDepartmentRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var (code, name) = (request.Code.Trim(), request.Name.Trim());
        if (await dbContext.Departments.AnyAsync(item => item.Code.ToUpper() == code.ToUpper(), cancellationToken))
        {
            return new(DirectoryOutcome.DuplicateCode, Error: $"A department with code '{code}' already exists.");
        }

        if (await MissingSiteAsync(request.SiteIds, cancellationToken) is { } missing)
        {
            return new(DirectoryOutcome.UnknownReference, Error: missing);
        }

        var department = new Department { Id = Guid.CreateVersion7(), Code = code, Name = name };
        dbContext.Departments.Add(department);
        foreach (var siteId in request.SiteIds.Distinct())
        {
            dbContext.DepartmentSites.Add(new DepartmentSite { DepartmentId = department.Id, SiteId = siteId });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await ReadDepartmentAsync(department.Id, cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", "Department", department.Id.ToString(), null, response, cancellationToken);
        return new(DirectoryOutcome.Success, response);
    }

    public async Task<DepartmentAdminResult> UpdateDepartmentAsync(
        Guid id,
        SaveDepartmentRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var department = await dbContext.Departments.Include(item => item.Sites)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (department is null)
        {
            return new(DirectoryOutcome.NotFound);
        }

        var (code, name) = (request.Code.Trim(), request.Name.Trim());
        if (await dbContext.Departments.AnyAsync(
                item => item.Code.ToUpper() == code.ToUpper() && item.Id != id, cancellationToken))
        {
            return new(DirectoryOutcome.DuplicateCode, Error: $"A department with code '{code}' already exists.");
        }

        if (await MissingSiteAsync(request.SiteIds, cancellationToken) is { } missing)
        {
            return new(DirectoryOutcome.UnknownReference, Error: missing);
        }

        var before = await ReadDepartmentAsync(id, cancellationToken);
        department.Code = code;
        department.Name = name;
        ReplaceLinks(
            department.Sites,
            request.SiteIds,
            link => link.SiteId,
            siteId => new DepartmentSite { DepartmentId = id, SiteId = siteId });
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = await ReadDepartmentAsync(id, cancellationToken);
        await auditService.WriteAsync(actor, "Updated", "Department", id.ToString(), before, after, cancellationToken);
        return new(DirectoryOutcome.Success, after);
    }

    public async Task<DirectoryOutcome> DeleteDepartmentAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var department = await dbContext.Departments.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (department is null)
        {
            return DirectoryOutcome.NotFound;
        }

        if (await dbContext.UserProfiles.AnyAsync(user => user.DepartmentId == id, cancellationToken))
        {
            return DirectoryOutcome.InUse;
        }

        foreach (var source in usageSources)
        {
            if (await source.CountByDepartmentAsync(id, cancellationToken) > 0)
            {
                return DirectoryOutcome.InUse;
            }
        }

        var before = await ReadDepartmentAsync(id, cancellationToken);
        dbContext.Departments.Remove(department);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Deleted", "Department", id.ToString(), before, null, cancellationToken);
        return DirectoryOutcome.Success;
    }

    public async Task<SiteAdminResult> CreateSiteAsync(
        SaveSiteRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var (code, name) = (request.Code.Trim(), request.Name.Trim());
        if (await dbContext.Sites.AnyAsync(item => item.Code.ToUpper() == code.ToUpper(), cancellationToken))
        {
            return new(DirectoryOutcome.DuplicateCode, Error: $"A location with code '{code}' already exists.");
        }

        if (await MissingDepartmentAsync(request.DepartmentIds, cancellationToken) is { } missing)
        {
            return new(DirectoryOutcome.UnknownReference, Error: missing);
        }

        var site = new Site { Id = Guid.CreateVersion7(), Code = code, Name = name };
        dbContext.Sites.Add(site);
        foreach (var departmentId in request.DepartmentIds.Distinct())
        {
            dbContext.DepartmentSites.Add(new DepartmentSite { DepartmentId = departmentId, SiteId = site.Id });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await ReadSiteAsync(site.Id, cancellationToken);
        await auditService.WriteAsync(actor, "Created", "Site", site.Id.ToString(), null, response, cancellationToken);
        return new(DirectoryOutcome.Success, response);
    }

    public async Task<SiteAdminResult> UpdateSiteAsync(
        Guid id,
        SaveSiteRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var site = await dbContext.Sites.Include(item => item.Departments)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (site is null)
        {
            return new(DirectoryOutcome.NotFound);
        }

        var (code, name) = (request.Code.Trim(), request.Name.Trim());
        if (await dbContext.Sites.AnyAsync(item => item.Code.ToUpper() == code.ToUpper() && item.Id != id, cancellationToken))
        {
            return new(DirectoryOutcome.DuplicateCode, Error: $"A location with code '{code}' already exists.");
        }

        if (await MissingDepartmentAsync(request.DepartmentIds, cancellationToken) is { } missing)
        {
            return new(DirectoryOutcome.UnknownReference, Error: missing);
        }

        var before = await ReadSiteAsync(id, cancellationToken);
        site.Code = code;
        site.Name = name;
        ReplaceLinks(
            site.Departments,
            request.DepartmentIds,
            link => link.DepartmentId,
            departmentId => new DepartmentSite { DepartmentId = departmentId, SiteId = id });
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = await ReadSiteAsync(id, cancellationToken);
        await auditService.WriteAsync(actor, "Updated", "Site", id.ToString(), before, after, cancellationToken);
        return new(DirectoryOutcome.Success, after);
    }

    public async Task<DirectoryOutcome> DeleteSiteAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var site = await dbContext.Sites.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (site is null)
        {
            return DirectoryOutcome.NotFound;
        }

        if (await dbContext.UserProfiles.AnyAsync(user => user.SiteId == id, cancellationToken))
        {
            return DirectoryOutcome.InUse;
        }

        foreach (var source in usageSources)
        {
            if (await source.CountBySiteAsync(id, cancellationToken) > 0)
            {
                return DirectoryOutcome.InUse;
            }
        }

        var before = await ReadSiteAsync(id, cancellationToken);
        dbContext.Sites.Remove(site);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Deleted", "Site", id.ToString(), before, null, cancellationToken);
        return DirectoryOutcome.Success;
    }

    /// <summary>
    /// Rewrites a link set in place, adding and removing only what changed, so an unrelated edit does
    /// not churn every row and fill the audit trail with movement that never happened.
    /// </summary>
    private void ReplaceLinks(
        ICollection<DepartmentSite> current,
        IReadOnlyList<Guid> wanted,
        Func<DepartmentSite, Guid> keyOf,
        Func<Guid, DepartmentSite> create)
    {
        var target = wanted.Distinct().ToHashSet();
        foreach (var link in current.Where(link => !target.Contains(keyOf(link))).ToList())
        {
            dbContext.DepartmentSites.Remove(link);
        }

        foreach (var key in target.Where(key => current.All(link => keyOf(link) != key)))
        {
            dbContext.DepartmentSites.Add(create(key));
        }
    }

    private async Task<string?> MissingSiteAsync(IReadOnlyList<Guid> siteIds, CancellationToken cancellationToken)
    {
        if (siteIds.Count == 0)
        {
            return null;
        }

        var known = await dbContext.Sites.Where(site => siteIds.Contains(site.Id))
            .Select(site => site.Id).ToListAsync(cancellationToken);
        return siteIds.Distinct().Except(known).Any() ? "One or more locations do not exist." : null;
    }

    private async Task<string?> MissingDepartmentAsync(
        IReadOnlyList<Guid> departmentIds,
        CancellationToken cancellationToken)
    {
        if (departmentIds.Count == 0)
        {
            return null;
        }

        var known = await dbContext.Departments.Where(department => departmentIds.Contains(department.Id))
            .Select(department => department.Id).ToListAsync(cancellationToken);
        return departmentIds.Distinct().Except(known).Any() ? "One or more departments do not exist." : null;
    }

    private async Task<Dictionary<Guid, int>> UserCountsByDepartmentAsync(CancellationToken cancellationToken) =>
        await dbContext.UserProfiles
            .GroupBy(user => user.DepartmentId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);

    private async Task<DepartmentAdminResponse> ReadDepartmentAsync(Guid id, CancellationToken cancellationToken)
    {
        var department = await dbContext.Departments.AsNoTracking()
            .Include(item => item.Sites).ThenInclude(link => link.Site)
            .SingleAsync(item => item.Id == id, cancellationToken);
        return MapDepartment(department, await UserCountsByDepartmentAsync(cancellationToken));
    }

    private async Task<SiteAdminResponse> ReadSiteAsync(Guid id, CancellationToken cancellationToken)
    {
        var site = await dbContext.Sites.AsNoTracking()
            .Include(item => item.Departments).ThenInclude(link => link.Department)
            .SingleAsync(item => item.Id == id, cancellationToken);
        var userCounts = await dbContext.UserProfiles
            .GroupBy(user => user.SiteId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);
        return MapSite(site, userCounts);
    }

    private static DepartmentAdminResponse MapDepartment(
        Department department,
        IReadOnlyDictionary<Guid, int> userCounts) => new(
        department.Id,
        department.Code,
        department.Name,
        [.. department.Sites.Where(link => link.Site is not null)
            .Select(link => new DirectorySite(link.Site!.Id, link.Site.Code, link.Site.Name))
            .OrderBy(site => site.Name)],
        userCounts.GetValueOrDefault(department.Id));

    private static SiteAdminResponse MapSite(Site site, IReadOnlyDictionary<Guid, int> userCounts) => new(
        site.Id,
        site.Code,
        site.Name,
        [.. site.Departments.Where(link => link.Department is not null)
            .Select(link => new DirectoryDepartment(link.Department!.Id, link.Department.Code, link.Department.Name))
            .OrderBy(department => department.Name)],
        userCounts.GetValueOrDefault(site.Id));
}
