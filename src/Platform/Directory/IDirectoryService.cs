namespace Platform.Directory;

/// <summary>
/// Platform's public read surface over people, departments, and sites. Modules resolve an owner or
/// a location through this interface — they never query the <c>platform</c> schema themselves.
/// </summary>
public interface IDirectoryService
{
    Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken cancellationToken);

    Task<DirectoryUser?> FindUserAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DirectoryDepartment>> ListDepartmentsAsync(CancellationToken cancellationToken);

    Task<DirectoryDepartment?> FindDepartmentAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DirectorySite>> ListSitesAsync(CancellationToken cancellationToken);

    Task<DirectorySite?> FindSiteAsync(Guid id, CancellationToken cancellationToken);
}
