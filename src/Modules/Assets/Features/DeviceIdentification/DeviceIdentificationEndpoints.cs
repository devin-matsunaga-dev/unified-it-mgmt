using System.Security.Claims;

using FluentValidation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Assets.Features.DeviceIdentification;

public static class DeviceIdentificationEndpoints
{
    public static IEndpointRouteBuilder MapDeviceIdentificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/device-identifications")
            .RequireAuthorization("CanManageAssets");

        // Stateless: the scans a technician has taken so far live in the client and are posted whole
        // each time. There is no session to open, resume or prune, and nothing is written until an
        // asset is created — which is the only durable act in this workflow.
        group.MapPost("/", async (IdentifyDeviceRequest request, IDeviceIdentificationService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new IdentifyValidator().ValidateAsync(request, cancellationToken);
            return validation.IsValid
                ? Results.Ok(await service.IdentifyAsync(request, cancellationToken))
                : Results.ValidationProblem(validation.ToDictionary());
        });

        var catalogue = endpoints.MapGroup("/api/product-catalog")
            .RequireAuthorization("CanManageAssets");

        catalogue.MapGet("/", async (string? search, IDeviceIdentificationService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.ListEntriesAsync(search, cancellationToken)));

        catalogue.MapPost("/", async (SaveProductCatalogEntryRequest request, ClaimsPrincipal user,
            IDeviceIdentificationService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveEntryValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());

            var result = await service.SaveEntryAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                ProductCatalogOutcome.Success => Results.Ok(result.Entry),
                ProductCatalogOutcome.Duplicate => Results.ValidationProblem(
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [nameof(SaveProductCatalogEntryRequest.ModelIdentifier)] = [result.Error!],
                    }),
                ProductCatalogOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Catalogue entry not found."),
                var outcome => throw new InvalidOperationException($"Unknown catalogue outcome '{outcome}'."),
            };
        });

        return endpoints;
    }

    private sealed class IdentifyValidator : AbstractValidator<IdentifyDeviceRequest>
    {
        public IdentifyValidator()
        {
            RuleFor(request => request.Scans).NotNull();
            // The per-scan length is enforced by the parser, which rejects rather than truncates; this
            // is the ceiling on how much work one request may ask for.
            RuleFor(request => request.Scans.Count)
                .InclusiveBetween(1, DeviceIdentificationService.MaxScans)
                .WithMessage($"Between 1 and {DeviceIdentificationService.MaxScans} scans.");
        }
    }

    private sealed class SaveEntryValidator : AbstractValidator<SaveProductCatalogEntryRequest>
    {
        public SaveEntryValidator()
        {
            RuleFor(request => request.ModelIdentifier).NotEmpty().MaximumLength(BarcodeParser.MaxLength);
            RuleFor(request => request.Manufacturer).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Model).NotEmpty().MaximumLength(200);
            RuleFor(request => request.ProductNumber).MaximumLength(BarcodeParser.MaxLength);
            RuleFor(request => request.DeviceType).MaximumLength(50);
        }
    }
}
