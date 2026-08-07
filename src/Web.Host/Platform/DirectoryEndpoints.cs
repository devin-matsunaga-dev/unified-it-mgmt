using Platform.Directory;

using Web.Host.Authentication;

namespace Web.Host.Platform;

/// <summary>
/// Read-only pickers for people, departments, and sites. Agent-only: the CI ownership form is the
/// only consumer today, so it carries the same policy as the CMDB it feeds.
/// </summary>
public static class DirectoryEndpoints
{
    public static IEndpointRouteBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/directory")
            .RequireAuthorization(AuthorizationPolicies.CanManageAssets);

        group.MapGet("/users", async (IDirectoryService directory, CancellationToken cancellationToken) =>
            Results.Ok(await directory.ListUsersAsync(cancellationToken)));

        group.MapGet("/departments", async (IDirectoryService directory, CancellationToken cancellationToken) =>
            Results.Ok(await directory.ListDepartmentsAsync(cancellationToken)));

        group.MapGet("/sites", async (IDirectoryService directory, CancellationToken cancellationToken) =>
            Results.Ok(await directory.ListSitesAsync(cancellationToken)));

        return endpoints;
    }
}
