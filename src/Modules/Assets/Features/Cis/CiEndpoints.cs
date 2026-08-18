using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Assets.Data;

namespace Modules.Assets.Features.Cis;

public static class CiEndpoints
{
    public static IEndpointRouteBuilder MapCiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/cis").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (CiType? type, string? search, bool? isActive, CiLifecycleState? lifecycleState,
                Guid? ownerUserId, Guid? departmentId, Guid? siteId, Guid? contractId,
                int? warrantyExpiringWithinDays, int? page, int? pageSize, string[]? customField,
                ICiService service, CancellationToken cancellationToken) =>
        {
            if (!TryParseCustomFields(customField, out var customFields))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["customField"] = ["Each value must read '<fieldId>:<value>' with a valid field id."],
                });
            }

            return Results.Ok(await service.ListAsync(
                new CiListRequest(
                    type, search, isActive, lifecycleState, ownerUserId, departmentId, siteId, contractId,
                    warrantyExpiringWithinDays, page ?? 1, pageSize ?? 25, customFields),
                cancellationToken));
        });

        group.MapGet("/{id:guid}", async (Guid id, ICiService service, CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } ci
                ? Results.Ok(ci)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "CI not found."));

        group.MapPost("/", async (CreateCiRequest request, ClaimsPrincipal user, ICiService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new CreateCiValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                CiOutcome.Success => Results.Created($"/api/cis/{result.Ci!.Id}", result.Ci),
                CiOutcome.InvalidAttributes or CiOutcome.InvalidCustomFields =>
                    Results.ValidationProblem(result.Errors!),
                CiOutcome.DuplicateIdentifier => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "CI identifier is already used.",
                    detail: result.Error),
                _ => throw new InvalidOperationException($"Unknown CI outcome '{result.Outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateCiRequest request, ClaimsPrincipal user,
            ICiService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateCiValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CiOutcome.Success => Results.Ok(result.Ci),
                CiOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "CI not found."),
                CiOutcome.InvalidAttributes or CiOutcome.InvalidCustomFields =>
                    Results.ValidationProblem(result.Errors!),
                CiOutcome.DuplicateIdentifier => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "CI identifier is already used.",
                    detail: result.Error),
                CiOutcome.Disposed => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "CI is disposed.",
                    detail: result.Error),
                _ => throw new InvalidOperationException($"Unknown CI outcome '{result.Outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ICiService service,
            CancellationToken cancellationToken) => await service.DeleteAsync(id, user, cancellationToken) switch
        {
            CiOutcome.Success => Results.NoContent(),
            CiOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "CI not found."),
            CiOutcome.InUse => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "CI is in use.",
                detail: "Remove the CI's relationships and unlink it from its tickets before deleting it."),
            var outcome => throw new InvalidOperationException($"Unknown CI outcome '{outcome}'."),
        });

        // The form needs the fixed attributes and the runtime custom fields together, so a field an
        // admin adds shows up without the client knowing how the two halves are stored.
        endpoints.MapGet("/api/ci-type-schemas", async (ICiService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.GetSchemasAsync(cancellationToken)))
            .RequireAuthorization("CanManageAssets");

        var admin = endpoints.MapGroup("/api/ci-custom-fields").RequireAuthorization("AdminOnly");

        admin.MapPost("/", async (CreateCiCustomFieldRequest request, ClaimsPrincipal user, ICiService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new CreateCiCustomFieldValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.AddFieldAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                CiOutcome.Success => Results.Created($"/api/ci-custom-fields/{result.Field!.Id}", result.Field),
                CiOutcome.DuplicateIdentifier => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Field key is already used.", detail: result.Error),
                _ => throw new InvalidOperationException($"Unknown CI outcome '{result.Outcome}'."),
            };
        });

        admin.MapPut("/{fieldId:guid}", async (Guid fieldId, UpdateCiCustomFieldRequest request,
            ClaimsPrincipal user, ICiService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateCiCustomFieldValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateFieldAsync(fieldId, request, user, cancellationToken);
            return result.Outcome switch
            {
                CiOutcome.Success => Results.Ok(result.Field),
                CiOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Custom field not found."),
                CiOutcome.InUse => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "An option is still in use.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown CI outcome '{outcome}'."),
            };
        });

        // Read by the edit dialog only, so the schema endpoint the asset list polls stays a plain read.
        admin.MapGet("/{fieldId:guid}/value-counts", async (Guid fieldId, ICiService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.GetFieldValueCountsAsync(fieldId, cancellationToken)));

        admin.MapDelete("/{fieldId:guid}", async (Guid fieldId, ClaimsPrincipal user, ICiService service,
            CancellationToken cancellationToken) =>
            await service.DeleteFieldAsync(fieldId, user, cancellationToken) switch
            {
                CiOutcome.Success => Results.NoContent(),
                CiOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Custom field not found."),
                CiOutcome.InUse => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Custom field is in use.",
                    detail: "Fields that already hold CI values cannot be deleted."),
                var outcome => throw new InvalidOperationException($"Unknown CI outcome '{outcome}'."),
            });

        return endpoints;
    }

    private sealed class CreateCiValidator : AbstractValidator<CreateCiRequest>
    {
        public CreateCiValidator()
        {
            RuleFor(request => request.Type).IsInEnum();
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.AssetTag).MaximumLength(64);
            RuleFor(request => request.SerialNumber).MaximumLength(128);
            RuleFor(request => request.Description).MaximumLength(2_000);
        }
    }

    private sealed class UpdateCiValidator : AbstractValidator<UpdateCiRequest>
    {
        public UpdateCiValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.AssetTag).MaximumLength(64);
            RuleFor(request => request.SerialNumber).MaximumLength(128);
            RuleFor(request => request.Description).MaximumLength(2_000);
        }
    }

    /// <summary>
    /// Reads repeated <c>?customField=&lt;fieldId&gt;:&lt;value&gt;</c> parameters.
    /// <para>
    /// Split on the FIRST colon only: a field id never contains one, but an option value may well —
    /// "Laptop: 14 inch" is a legitimate thing for somebody to have typed into the options list.
    /// </para>
    /// </summary>
    private static bool TryParseCustomFields(string[]? raw, out IReadOnlyList<CiCustomFieldFilter>? parsed)
    {
        parsed = null;
        if (raw is null || raw.Length == 0)
        {
            return true;
        }

        var filters = new List<CiCustomFieldFilter>(raw.Length);
        foreach (var token in raw)
        {
            var separator = token.IndexOf(':');
            if (separator <= 0 || separator == token.Length - 1)
            {
                return false;
            }

            if (!Guid.TryParse(token[..separator], out var fieldId))
            {
                return false;
            }

            filters.Add(new CiCustomFieldFilter(fieldId, token[(separator + 1)..]));
        }

        parsed = filters;
        return true;
    }

    private sealed class UpdateCiCustomFieldValidator : AbstractValidator<UpdateCiCustomFieldRequest>
    {
        public UpdateCiCustomFieldValidator()
        {
            RuleFor(request => request.Label).NotEmpty().MaximumLength(100);
            RuleFor(request => request.SortOrder).InclusiveBetween(0, 10_000);
            RuleForEach(request => request.Options).NotEmpty().MaximumLength(100)
                .When(request => request.Options is not null);
        }
    }

    private sealed class CreateCiCustomFieldValidator : AbstractValidator<CreateCiCustomFieldRequest>
    {
        public CreateCiCustomFieldValidator()
        {
            RuleFor(request => request.CiType).IsInEnum();
            RuleFor(request => request.Key).NotEmpty().MaximumLength(50)
                .Matches("^[a-zA-Z][a-zA-Z0-9_]*$")
                .WithMessage("Key must start with a letter and contain only letters, digits, and underscores.");
            RuleFor(request => request.Label).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Type).IsInEnum();
            RuleFor(request => request.SortOrder).InclusiveBetween(0, 10_000);
            RuleFor(request => request.Options)
                .Must(options => options is { Count: > 0 } && options.All(option => !string.IsNullOrWhiteSpace(option)))
                .When(request => request.Type == CiCustomFieldType.Select)
                .WithMessage("Select fields require at least one non-empty option.");
            RuleForEach(request => request.Options).MaximumLength(100)
                .When(request => request.Options is not null);
        }
    }
}
