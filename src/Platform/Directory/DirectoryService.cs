using Microsoft.EntityFrameworkCore;

using Platform.Data;

namespace Platform.Directory;

public sealed class DirectoryService(PlatformDbContext dbContext) : IDirectoryService
{
    public async Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken cancellationToken) =>
        await Users(dbContext.UserProfiles.OrderBy(user => user.DisplayName)).ToListAsync(cancellationToken);

    public async Task<DirectoryUser?> FindUserAsync(Guid id, CancellationToken cancellationToken) =>
        await Users(dbContext.UserProfiles.Where(user => user.Id == id))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DirectoryDepartment>> ListDepartmentsAsync(CancellationToken cancellationToken) =>
        await Departments(dbContext.Departments.OrderBy(department => department.Name))
            .ToListAsync(cancellationToken);

    public async Task<DirectoryDepartment?> FindDepartmentAsync(Guid id, CancellationToken cancellationToken) =>
        await Departments(dbContext.Departments.Where(department => department.Id == id))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DirectorySite>> ListSitesAsync(CancellationToken cancellationToken) =>
        await Sites(dbContext.Sites.OrderBy(site => site.Name)).ToListAsync(cancellationToken);

    public async Task<DirectorySite?> FindSiteAsync(Guid id, CancellationToken cancellationToken) =>
        await Sites(dbContext.Sites.Where(site => site.Id == id)).SingleOrDefaultAsync(cancellationToken);

    // Filtering and ordering happen on the entity before the record projection: EF cannot translate a
    // predicate or an order applied to a constructed record.
    private static IQueryable<DirectoryUser> Users(IQueryable<UserProfile> source) => source
        .Select(user => new DirectoryUser(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Email,
            user.Role,
            user.SiteId,
            user.Site!.Name,
            user.DepartmentId,
            user.Department!.Name));

    private static IQueryable<DirectoryDepartment> Departments(IQueryable<Department> source) => source
        .Select(department => new DirectoryDepartment(department.Id, department.Code, department.Name));

    private static IQueryable<DirectorySite> Sites(IQueryable<Site> source) => source
        .Select(site => new DirectorySite(site.Id, site.Code, site.Name));
}
