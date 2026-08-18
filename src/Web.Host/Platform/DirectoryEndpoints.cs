using System.Security.Claims;

using FluentValidation;
using Platform.Directory;

using Web.Host.Authentication;

namespace Web.Host.Platform;

/// <summary>
/// Read-only pickers for people, departments, and sites, plus the AdminOnly administration of the
/// organisation chart behind them (Settings). The reads stay on the assets policy because the CI
/// ownership form is their oldest consumer; every write is AdminOnly and audited.
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

        var admin = endpoints.MapGroup("/api/directory")
            .RequireAuthorization(AuthorizationPolicies.AdminOnly);

        admin.MapGet("/admin/departments", async (IDirectoryAdminService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListDepartmentsAsync(cancellationToken)));

        admin.MapPost("/admin/departments", async (SaveDepartmentRequest request, ClaimsPrincipal user,
            IDirectoryAdminService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveDepartmentValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateDepartmentAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                DirectoryOutcome.Success => Results.Created(
                    $"/api/directory/admin/departments/{result.Department!.Id}", result.Department),
                DirectoryOutcome.DuplicateCode => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Department code is already used.", detail: result.Error),
                DirectoryOutcome.UnknownReference => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.SiteIds)] = [result.Error!] }),
                var outcome => throw new InvalidOperationException($"Unknown directory outcome '{outcome}'."),
            };
        });

        admin.MapPut("/admin/departments/{id:guid}", async (Guid id, SaveDepartmentRequest request,
            ClaimsPrincipal user, IDirectoryAdminService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveDepartmentValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateDepartmentAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                DirectoryOutcome.Success => Results.Ok(result.Department),
                DirectoryOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Department not found."),
                DirectoryOutcome.DuplicateCode => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Department code is already used.", detail: result.Error),
                DirectoryOutcome.UnknownReference => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.SiteIds)] = [result.Error!] }),
                var outcome => throw new InvalidOperationException($"Unknown directory outcome '{outcome}'."),
            };
        });

        admin.MapDelete("/admin/departments/{id:guid}", async (Guid id, ClaimsPrincipal user,
            IDirectoryAdminService service, CancellationToken cancellationToken) =>
            await service.DeleteDepartmentAsync(id, user, cancellationToken) switch
            {
                DirectoryOutcome.Success => Results.NoContent(),
                DirectoryOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Department not found."),
                DirectoryOutcome.InUse => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Department is in use.",
                    detail: "People or configuration items still belong to this department; move them first."),
                var outcome => throw new InvalidOperationException($"Unknown directory outcome '{outcome}'."),
            });

        admin.MapGet("/admin/sites", async (IDirectoryAdminService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListSitesAsync(cancellationToken)));

        admin.MapPost("/admin/sites", async (SaveSiteRequest request, ClaimsPrincipal user,
            IDirectoryAdminService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveSiteValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateSiteAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                DirectoryOutcome.Success => Results.Created(
                    $"/api/directory/admin/sites/{result.Site!.Id}", result.Site),
                DirectoryOutcome.DuplicateCode => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Location code is already used.", detail: result.Error),
                DirectoryOutcome.UnknownReference => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.DepartmentIds)] = [result.Error!] }),
                var outcome => throw new InvalidOperationException($"Unknown directory outcome '{outcome}'."),
            };
        });

        admin.MapPut("/admin/sites/{id:guid}", async (Guid id, SaveSiteRequest request, ClaimsPrincipal user,
            IDirectoryAdminService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveSiteValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateSiteAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                DirectoryOutcome.Success => Results.Ok(result.Site),
                DirectoryOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Location not found."),
                DirectoryOutcome.DuplicateCode => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Location code is already used.", detail: result.Error),
                DirectoryOutcome.UnknownReference => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.DepartmentIds)] = [result.Error!] }),
                var outcome => throw new InvalidOperationException($"Unknown directory outcome '{outcome}'."),
            };
        });

        admin.MapDelete("/admin/sites/{id:guid}", async (Guid id, ClaimsPrincipal user,
            IDirectoryAdminService service, CancellationToken cancellationToken) =>
            await service.DeleteSiteAsync(id, user, cancellationToken) switch
            {
                DirectoryOutcome.Success => Results.NoContent(),
                DirectoryOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Location not found."),
                DirectoryOutcome.InUse => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Location is in use.",
                    detail: "People or configuration items are still at this location; move them first."),
                var outcome => throw new InvalidOperationException($"Unknown directory outcome '{outcome}'."),
            });

        return endpoints;
    }

    private sealed class SaveDepartmentValidator : AbstractValidator<SaveDepartmentRequest>
    {
        public SaveDepartmentValidator()
        {
            RuleFor(request => request.Code).NotEmpty().MaximumLength(50);
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleForEach(request => request.SiteIds).NotEqual(Guid.Empty);
        }
    }

    private sealed class SaveSiteValidator : AbstractValidator<SaveSiteRequest>
    {
        public SaveSiteValidator()
        {
            RuleFor(request => request.Code).NotEmpty().MaximumLength(50);
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleForEach(request => request.DepartmentIds).NotEqual(Guid.Empty);
        }
    }
}
