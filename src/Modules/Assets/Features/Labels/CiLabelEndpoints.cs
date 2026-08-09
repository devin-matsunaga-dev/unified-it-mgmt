using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Assets.Features.Labels;

public static class CiLabelEndpoints
{
    private const string Pdf = "application/pdf";

    public static IEndpointRouteBuilder MapCiLabelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/cis/{id:guid}/label", async (Guid id, CiLabelSize? size, ICiLabelService service,
                CancellationToken cancellationToken) =>
                ToResult(await service.RenderAsync([id], size ?? CiLabelSize.Standard, cancellationToken)))
            .RequireAuthorization("CanManageAssets");

        // A batch is a POST because the selection is a body of up to 200 ids, not a query string, and
        // because "print these" is an action rather than a document that already exists somewhere.
        endpoints.MapPost("/api/ci-labels/sheets", async (CiLabelSheetRequest request, ICiLabelService service,
                CancellationToken cancellationToken) =>
            {
                var validation = await new CiLabelSheetValidator().ValidateAsync(request, cancellationToken);
                if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
                return ToResult(await service.RenderAsync(request.CiIds, request.Size, cancellationToken));
            })
            .RequireAuthorization("CanManageAssets");

        // The scan page's one call: whatever the camera or the wedge scanner produced, in, and the CI
        // it names, out.
        endpoints.MapGet("/api/cis/lookup", async (string? code, ICiLabelService service,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["code"] = ["Scan or type a code to look up."],
                    });
                }

                return await service.LookupAsync(code, cancellationToken) is { } ci
                    ? Results.Ok(ci)
                    : Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "No asset matches that code.",
                        detail: $"Nothing in the CMDB has the id, asset tag, or serial number '{code.Trim()}'.");
            })
            .RequireAuthorization("CanManageAssets");

        return endpoints;
    }

    private static IResult ToResult(CiLabelResult result) => result.Outcome switch
    {
        CiLabelOutcome.Success => Results.File(result.Content!, Pdf, result.FileName),
        CiLabelOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound, title: "CI not found.", detail: result.Error),
        CiLabelOutcome.Invalid => Results.ValidationProblem(result.Errors!),
        var outcome => throw new InvalidOperationException($"Unknown label outcome '{outcome}'."),
    };

    private sealed class CiLabelSheetValidator : AbstractValidator<CiLabelSheetRequest>
    {
        public CiLabelSheetValidator()
        {
            RuleFor(request => request.Size).IsInEnum();
            RuleFor(request => request.CiIds).NotEmpty()
                .WithMessage("Select at least one configuration item to print.");
            RuleFor(request => request.CiIds).Must(ids => ids.Count <= CiLabelService.MaximumSheetSize)
                .When(request => request.CiIds is not null)
                .WithMessage($"A label sheet holds at most {CiLabelService.MaximumSheetSize} labels.");
        }
    }
}
