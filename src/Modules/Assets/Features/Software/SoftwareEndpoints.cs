using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;

namespace Modules.Assets.Features.Software;

public static class SoftwareEndpoints
{
    public static IEndpointRouteBuilder MapSoftwareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapProducts(endpoints);
        MapRules(endpoints);
        MapInstalls(endpoints);
        MapImports(endpoints);
        MapLicensePools(endpoints);
        MapCompliance(endpoints);
        return endpoints;
    }

    private static void MapProducts(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/software-products").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (string? search, bool? isActive, int? page, int? pageSize,
                ISoftwareCatalogService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListProductsAsync(
                new SoftwareProductListRequest(search, isActive, page ?? 1, pageSize ?? 25), cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, ISoftwareCatalogService service,
                CancellationToken cancellationToken) =>
            await service.GetProductAsync(id, cancellationToken) is { } product
                ? Results.Ok(product)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Software product not found."));

        group.MapPost("/", async (CreateSoftwareProductRequest request, ClaimsPrincipal user,
            ISoftwareCatalogService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateSoftwareProductValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateProductAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                SoftwareOutcome.Success => Results.Created($"/api/software-products/{result.Product!.Id}", result.Product),
                SoftwareOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Software product already exists.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown software outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateSoftwareProductRequest request, ClaimsPrincipal user,
            ISoftwareCatalogService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateSoftwareProductValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateProductAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                SoftwareOutcome.Success => Results.Ok(result.Product),
                SoftwareOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Software product not found."),
                SoftwareOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Software product already exists.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown software outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ISoftwareCatalogService service,
            CancellationToken cancellationToken) => await service.DeleteProductAsync(id, user, cancellationToken) switch
        {
            SoftwareOutcome.Success => Results.NoContent(),
            SoftwareOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Software product not found."),
            SoftwareOutcome.InUse => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Software product is in use.",
                detail: "Installs, licence pools or catalogue rules still name this product. "
                    + "Deactivate it instead, which keeps the history resolvable."),
            var outcome => throw new InvalidOperationException($"Unknown software outcome '{outcome}'."),
        });
    }

    private static void MapRules(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/software-normalisation-rules").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (Guid? productId, ISoftwareCatalogService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.ListRulesAsync(productId, cancellationToken)));

        group.MapPost("/", async (CreateSoftwareRuleRequest request, ClaimsPrincipal user,
            ISoftwareCatalogService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateSoftwareRuleValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateRuleAsync(request, user, cancellationToken);
            return RuleResult(result, created: true);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateSoftwareRuleRequest request, ClaimsPrincipal user,
            ISoftwareCatalogService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateSoftwareRuleValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            return RuleResult(await service.UpdateRuleAsync(id, request, user, cancellationToken), created: false);
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ISoftwareCatalogService service,
            CancellationToken cancellationToken) => await service.DeleteRuleAsync(id, user, cancellationToken) switch
        {
            SoftwareOutcome.Success => Results.NoContent(),
            SoftwareOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Normalisation rule not found."),
            var outcome => throw new InvalidOperationException($"Unknown software outcome '{outcome}'."),
        });
    }

    private static void MapInstalls(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/installed-software").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (Guid? ciId, Guid? productId, bool? isNormalised, string? search, int? page,
                int? pageSize, ISoftwareCatalogService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListInstallsAsync(
                new InstalledSoftwareListRequest(ciId, productId, isNormalised, search, page ?? 1, pageSize ?? 25),
                cancellationToken)));

        group.MapGet("/unrecognised", async (int? limit, ISoftwareCatalogService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.ListUnrecognisedAsync(limit ?? 25, cancellationToken)));

        // Re-running the catalogue is a POST because it writes: a rule added today has to be able to
        // reach the inventory imported last month, or the catalogue only ever applies to the future.
        group.MapPost("/normalisations", async (ClaimsPrincipal user, ISoftwareCatalogService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.NormaliseAsync(user, cancellationToken)));
    }

    private static void MapImports(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/software-imports")
            .RequireAuthorization("CanManageAssets")
            .DisableAntiforgery();

        // The dry run and the commit take the same file and answer the same report, so the preview is
        // literally what the commit will do — WP-2.5's rule, and the reason nothing is parked server-side.
        group.MapPost("/preview", async (IFormFile file, ISoftwareImportService service,
                CancellationToken cancellationToken) =>
            ImportResult(await service.PreviewAsync(file, cancellationToken)));

        group.MapPost("/commit", async (IFormFile file, ClaimsPrincipal user, ISoftwareImportService service,
                CancellationToken cancellationToken) =>
            ImportResult(await service.CommitAsync(file, user, cancellationToken)));
    }

    private static void MapLicensePools(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/license-pools").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (string? search, Guid? productId, ContractExpiryStatus? status, bool? isActive,
                int? page, int? pageSize, ILicensingService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListPoolsAsync(
                new LicensePoolListRequest(search, productId, status, isActive, page ?? 1, pageSize ?? 25),
                cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, ILicensingService service, CancellationToken cancellationToken) =>
            await service.GetPoolAsync(id, cancellationToken) is { } pool
                ? Results.Ok(pool)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Licence pool not found."));

        group.MapPost("/", async (CreateLicensePoolRequest request, ClaimsPrincipal user, ILicensingService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new CreateLicensePoolValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreatePoolAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                SoftwareOutcome.Success => Results.Created($"/api/license-pools/{result.Pool!.Id}", result.Pool),
                SoftwareOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Software product not found.",
                    detail: result.Error),
                SoftwareOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Licence pool name is already used.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown software outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateLicensePoolRequest request, ClaimsPrincipal user,
            ILicensingService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateLicensePoolValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdatePoolAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                SoftwareOutcome.Success => Results.Ok(result.Pool),
                SoftwareOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Licence pool not found.",
                    detail: result.Error),
                SoftwareOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Licence pool name is already used.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown software outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ILicensingService service,
            CancellationToken cancellationToken) => await service.DeletePoolAsync(id, user, cancellationToken) switch
        {
            SoftwareOutcome.Success => Results.NoContent(),
            SoftwareOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Licence pool not found."),
            var outcome => throw new InvalidOperationException($"Unknown software outcome '{outcome}'."),
        });
    }

    private static void MapCompliance(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/software-compliance").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (SoftwareComplianceState? state, string? search, ILicensingService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.ReportAsync(new SoftwareComplianceRequest(state, search), cancellationToken)));

        // The scheduled pass runs daily; the manual trigger exists for the same reason WP-2.6's does —
        // the dev database is recreated on most AppHost restarts, so a pool created by hand would never
        // survive until the next scheduled run. The pass is idempotent within a day.
        group.MapPost("/runs", async (ISoftwareComplianceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.RunAsync(cancellationToken)));
    }

    private static IResult RuleResult(SoftwareRuleResult result, bool created) => result.Outcome switch
    {
        SoftwareOutcome.Success when created =>
            Results.Created($"/api/software-normalisation-rules/{result.Rule!.Id}", result.Rule),
        SoftwareOutcome.Success => Results.Ok(result.Rule),
        SoftwareOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Normalisation rule or product not found.",
            detail: result.Error),
        SoftwareOutcome.Duplicate => Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "That pattern is already in the catalogue.",
            detail: result.Error),
        var outcome => throw new InvalidOperationException($"Unknown software outcome '{outcome}'."),
    };

    private static IResult ImportResult(SoftwareImportResult result) => result.Outcome switch
    {
        SoftwareImportOutcome.Success => Results.Ok(result.Report),
        SoftwareImportOutcome.InvalidFile => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "The inventory file could not be used.",
            detail: result.Error),
        var outcome => throw new InvalidOperationException($"Unknown import outcome '{outcome}'."),
    };

    private sealed class CreateSoftwareProductValidator : AbstractValidator<CreateSoftwareProductRequest>
    {
        public CreateSoftwareProductValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Publisher).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Category).MaximumLength(100);
            RuleFor(request => request.Notes).MaximumLength(2_000);
        }
    }

    private sealed class UpdateSoftwareProductValidator : AbstractValidator<UpdateSoftwareProductRequest>
    {
        public UpdateSoftwareProductValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Publisher).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Category).MaximumLength(100);
            RuleFor(request => request.Notes).MaximumLength(2_000);
        }
    }

    private sealed class CreateSoftwareRuleValidator : AbstractValidator<CreateSoftwareRuleRequest>
    {
        public CreateSoftwareRuleValidator()
        {
            RuleFor(request => request.ProductId).NotEqual(Guid.Empty);
            RuleFor(request => request.MatchKind).IsInEnum();
            RuleFor(request => request.Pattern).NotEmpty().MaximumLength(300);
            RuleFor(request => request.Priority).InclusiveBetween(0, 1_000);
        }
    }

    private sealed class UpdateSoftwareRuleValidator : AbstractValidator<UpdateSoftwareRuleRequest>
    {
        public UpdateSoftwareRuleValidator()
        {
            RuleFor(request => request.ProductId).NotEqual(Guid.Empty);
            RuleFor(request => request.MatchKind).IsInEnum();
            RuleFor(request => request.Pattern).NotEmpty().MaximumLength(300);
            RuleFor(request => request.Priority).InclusiveBetween(0, 1_000);
        }
    }

    private sealed class CreateLicensePoolValidator : AbstractValidator<CreateLicensePoolRequest>
    {
        public CreateLicensePoolValidator()
        {
            RuleFor(request => request.ProductId).NotEqual(Guid.Empty);
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Reference).MaximumLength(100);

            // A pool of zero is a real thing to record — an agreement whose seats have not been bought
            // yet — but a negative one is not an entitlement at all.
            RuleFor(request => request.Entitlements).InclusiveBetween(0, 1_000_000);
            RuleFor(request => request.Notes).MaximumLength(2_000);
            RuleFor(request => request.ExpiresAt).GreaterThanOrEqualTo(request => request.PurchaseDate!.Value)
                .When(request => request.PurchaseDate is not null && request.ExpiresAt is not null)
                .WithMessage("A licence cannot expire before it was bought.");
        }
    }

    private sealed class UpdateLicensePoolValidator : AbstractValidator<UpdateLicensePoolRequest>
    {
        public UpdateLicensePoolValidator()
        {
            RuleFor(request => request.ProductId).NotEqual(Guid.Empty);
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Reference).MaximumLength(100);
            RuleFor(request => request.Entitlements).InclusiveBetween(0, 1_000_000);
            RuleFor(request => request.Notes).MaximumLength(2_000);
            RuleFor(request => request.ExpiresAt).GreaterThanOrEqualTo(request => request.PurchaseDate!.Value)
                .When(request => request.PurchaseDate is not null && request.ExpiresAt is not null)
                .WithMessage("A licence cannot expire before it was bought.");
        }
    }
}
