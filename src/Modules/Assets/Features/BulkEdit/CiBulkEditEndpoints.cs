using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Assets.Features.BulkEdit;

public static class CiBulkEditEndpoints
{
    public static IEndpointRouteBuilder MapCiBulkEditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A partial success is the normal case, so this answers 200 with a per-CI report rather than
        // failing the whole selection on the first refused row.
        endpoints.MapPost("/api/cis/bulk-edit", async (
                BulkEditCisRequest request, ClaimsPrincipal user, ICiBulkEditService service,
                CancellationToken cancellationToken) =>
            {
                var validation = await new BulkEditCisValidator().ValidateAsync(request, cancellationToken);
                return validation.IsValid
                    ? Results.Ok(await service.ApplyAsync(request, user, cancellationToken))
                    : Results.ValidationProblem(validation.ToDictionary());
            })
            .RequireAuthorization("CanManageAssets");

        return endpoints;
    }

    private sealed class BulkEditCisValidator : AbstractValidator<BulkEditCisRequest>
    {
        public BulkEditCisValidator()
        {
            RuleFor(request => request.CiIds).NotEmpty()
                .WithMessage("Select at least one configuration item.");
            RuleFor(request => request.CiIds)
                .Must(ids => ids.Distinct().Count() <= CiBulkEditService.MaximumSelection)
                .WithMessage($"A bulk edit covers at most {CiBulkEditService.MaximumSelection} configuration items.");
            RuleFor(request => request.LifecycleState).IsInEnum()
                .When(request => request.LifecycleState is not null);
            RuleFor(request => request.Note).MaximumLength(500);
            RuleFor(request => request)
                .Must(request => request.Ownership is not null || request.LifecycleState is not null)
                .WithMessage("Choose an ownership change, a lifecycle state, or both.")
                .OverridePropertyName(nameof(BulkEditCisRequest.Ownership));
        }
    }
}
