using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Assets.Features.Relationships;

public static class CiRelationshipEndpoints
{
    public static IEndpointRouteBuilder MapCiRelationshipEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/cis/{id:guid}").RequireAuthorization("CanManageAssets");

        group.MapGet("/relationships", async (Guid id, ICiRelationshipService service,
                CancellationToken cancellationToken) =>
            await service.GetForCiAsync(id, cancellationToken) is { } relationships
                ? Results.Ok(relationships)
                : NotFound("CI not found."));

        group.MapPost("/relationships", async (Guid id, CreateCiRelationshipRequest request, ClaimsPrincipal user,
            ICiRelationshipService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateCiRelationshipValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CiRelationshipOutcome.Success =>
                    Results.Created($"/api/ci-relationships/{result.Relationship!.Id}", result.Relationship),
                CiRelationshipOutcome.CiNotFound => NotFound("CI not found."),
                CiRelationshipOutcome.InvalidTarget => Results.ValidationProblem(result.Errors!),
                CiRelationshipOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Relationship already exists.",
                    detail: result.Error),
                CiRelationshipOutcome.Disposed => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "CI is disposed.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown relationship outcome '{outcome}'."),
            };
        });

        // Ancestors walk toward what the CI depends on; descendants walk toward what depends on it.
        group.MapGet("/ancestors", (Guid id, int? maxDepth, ICiRelationshipService service,
                CancellationToken cancellationToken) =>
            GraphAsync(service, id, CiGraphDirection.Ancestors, maxDepth, cancellationToken));

        group.MapGet("/descendants", (Guid id, int? maxDepth, ICiRelationshipService service,
                CancellationToken cancellationToken) =>
            GraphAsync(service, id, CiGraphDirection.Descendants, maxDepth, cancellationToken));

        group.MapGet("/impacted-by", async (Guid id, int? maxDepth, ICiRelationshipService service,
                CancellationToken cancellationToken) =>
            await service.GetImpactedByAsync(id, maxDepth ?? CiGraphQuery.DefaultDepth, cancellationToken) is { } graph
                ? Results.Ok(graph)
                : NotFound("CI not found."));

        endpoints.MapDelete("/api/ci-relationships/{relationshipId:guid}", async (Guid relationshipId,
                ClaimsPrincipal user, ICiRelationshipService service, CancellationToken cancellationToken) =>
            await service.DeleteAsync(relationshipId, user, cancellationToken) switch
            {
                CiRelationshipOutcome.Success => Results.NoContent(),
                CiRelationshipOutcome.RelationshipNotFound => NotFound("Relationship not found."),
                var outcome => throw new InvalidOperationException($"Unknown relationship outcome '{outcome}'."),
            })
            .RequireAuthorization("CanManageAssets");

        return endpoints;
    }

    private static async Task<IResult> GraphAsync(
        ICiRelationshipService service,
        Guid ciId,
        CiGraphDirection direction,
        int? maxDepth,
        CancellationToken cancellationToken) =>
        await service.GetGraphAsync(ciId, direction, maxDepth ?? CiGraphQuery.DefaultDepth, cancellationToken)
            is { } graph
            ? Results.Ok(graph)
            : NotFound("CI not found.");

    private static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    private sealed class CreateCiRelationshipValidator : AbstractValidator<CreateCiRelationshipRequest>
    {
        public CreateCiRelationshipValidator()
        {
            RuleFor(request => request.TargetCiId).NotEqual(Guid.Empty);
            RuleFor(request => request.Type).IsInEnum();
            RuleFor(request => request.Description).MaximumLength(500);
        }
    }
}
