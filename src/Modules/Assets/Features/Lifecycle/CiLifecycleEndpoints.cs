using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Assets.Features.Cis;

namespace Modules.Assets.Features.Lifecycle;

public static class CiLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapCiLifecycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/cis/{id:guid}").RequireAuthorization("CanManageAssets");

        group.MapPost("/lifecycle-transitions", async (Guid id, TransitionCiRequest request, ClaimsPrincipal user,
            ICiLifecycleService service, CancellationToken cancellationToken) =>
        {
            var validation = await new TransitionCiValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.TransitionAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CiOutcome.Success => Results.Ok(result.Ci),
                CiOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "CI not found."),
                CiOutcome.IllegalTransition => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Lifecycle transition is not allowed.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown CI outcome '{outcome}'."),
            };
        });

        group.MapGet("/lifecycle-transitions", async (Guid id, ICiLifecycleService service,
                CancellationToken cancellationToken) =>
            await service.GetHistoryAsync(id, cancellationToken) is { } history
                ? Results.Ok(history)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "CI not found."));

        group.MapPut("/assignment", async (Guid id, AssignCiRequest request, ClaimsPrincipal user,
            ICiLifecycleService service, CancellationToken cancellationToken) =>
        {
            var validation = await new AssignCiValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.AssignAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CiOutcome.Success => Results.Ok(result.Ci),
                CiOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "CI not found."),
                CiOutcome.UnknownAssignee => Results.ValidationProblem(result.Errors!),
                CiOutcome.Disposed => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "CI is disposed.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown CI outcome '{outcome}'."),
            };
        });

        group.MapGet("/assignments", async (Guid id, ICiLifecycleService service,
                CancellationToken cancellationToken) =>
            await service.GetAssignmentsAsync(id, cancellationToken) is { } log
                ? Results.Ok(log)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "CI not found."));

        // The lifecycle graph is data, so the form reads the legal next states rather than
        // duplicating the guard in the browser.
        endpoints.MapGet("/api/ci-lifecycle-states", async (ICiLifecycleService service,
                CancellationToken cancellationToken) =>
                Results.Ok(await service.GetStatesAsync(cancellationToken)))
            .RequireAuthorization("CanManageAssets");

        return endpoints;
    }

    private sealed class TransitionCiValidator : AbstractValidator<TransitionCiRequest>
    {
        public TransitionCiValidator()
        {
            RuleFor(request => request.TargetState).IsInEnum();
            RuleFor(request => request.Note).MaximumLength(1_000);
        }
    }

    private sealed class AssignCiValidator : AbstractValidator<AssignCiRequest>
    {
        public AssignCiValidator()
        {
            RuleFor(request => request.Note).MaximumLength(1_000);
            RuleFor(request => request.OwnerUserId).NotEqual(Guid.Empty);
            RuleFor(request => request.DepartmentId).NotEqual(Guid.Empty);
            RuleFor(request => request.SiteId).NotEqual(Guid.Empty);
        }
    }
}
