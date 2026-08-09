using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modules.Assets.Data;

namespace Modules.Assets.Features.Import;

public static class CiImportEndpoints
{
    /// <summary>
    /// The mapping travels as a JSON form field beside the upload: the wizard re-sends the file it
    /// already holds at each step, so nothing half-imported has to be parked on the server between them.
    /// </summary>
    internal static readonly JsonSerializerOptions MappingSerializerOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static IEndpointRouteBuilder MapCiImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ci-imports")
            .RequireAuthorization("CanManageAssets")
            .DisableAntiforgery();

        group.MapPost("/columns", async (
            IFormFile file, [FromForm] string? type, ICiImportService service, CancellationToken cancellationToken) =>
        {
            if (!CiImportTypeSelection.TryParse(type, out var selected))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["type"] = [$"Choose a CI type for the import, or '{CiImportTypeSelection.Mixed}' to read it from a column."],
                });
            }

            var result = await service.InspectAsync(file, selected, cancellationToken);
            return result.Outcome switch
            {
                CiImportOutcome.Success => Results.Ok(result.Columns),
                CiImportOutcome.InvalidFile => InvalidFile(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown import outcome '{outcome}'."),
            };
        });

        group.MapPost("/preview", async (
            IFormFile file, [FromForm] string mapping, ICiImportService service, CancellationToken cancellationToken) =>
        {
            if (!TryReadMapping(mapping, out var parsed, out var mappingProblem))
            {
                return mappingProblem;
            }

            return ToResult(await service.PreviewAsync(file, parsed, cancellationToken));
        });

        group.MapPost("/commit", async (
            IFormFile file, [FromForm] string mapping, ClaimsPrincipal user, ICiImportService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadMapping(mapping, out var parsed, out var mappingProblem))
            {
                return mappingProblem;
            }

            return ToResult(await service.CommitAsync(file, parsed, user, cancellationToken));
        });

        return endpoints;
    }

    private static bool TryReadMapping(string mapping, out CiImportMapping parsed, out IResult problem)
    {
        parsed = new(default, new Dictionary<string, string>());
        MappingPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MappingPayload>(mapping, MappingSerializerOptions);
        }
        catch (JsonException)
        {
            problem = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["mapping"] = ["The column mapping could not be read."],
            });
            return false;
        }

        if (payload is not null)
        {
            if (!CiImportTypeSelection.TryParse(payload.Type, out var selected))
            {
                problem = Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["mapping.type"] =
                        [$"Choose a CI type for the import, or '{CiImportTypeSelection.Mixed}' to read it from a column."],
                });
                return false;
            }

            parsed = new(selected, payload.Columns ?? [], payload.AcceptInferredTypes ?? false);
        }

        var validation = new CiImportMappingValidator().Validate(parsed);
        if (!validation.IsValid)
        {
            problem = Results.ValidationProblem(validation.ToDictionary());
            return false;
        }

        problem = Results.Empty;
        return true;
    }

    private static IResult ToResult(CiImportResult result) => result.Outcome switch
    {
        CiImportOutcome.Success => Results.Ok(result.Report),
        CiImportOutcome.InvalidFile => InvalidFile(result.Error),
        CiImportOutcome.InvalidMapping => Results.ValidationProblem(result.Errors!),
        var outcome => throw new InvalidOperationException($"Unknown import outcome '{outcome}'."),
    };

    private static IResult InvalidFile(string? error) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "The import file could not be used.",
        detail: error);

    /// <summary>The wire shape, whose members may be absent, kept apart from the validated mapping.</summary>
    private sealed record MappingPayload(string? Type, Dictionary<string, string>? Columns, bool? AcceptInferredTypes);

    private sealed class CiImportMappingValidator : AbstractValidator<CiImportMapping>
    {
        public CiImportMappingValidator()
        {
            // A null type is the mixed-type import; anything else was already checked when it was parsed.
            RuleFor(mapping => mapping.Type).Must(type => type is null || Enum.IsDefined(type.Value))
                .WithMessage("Choose a CI type for the import.");
            RuleFor(mapping => mapping.Columns).NotEmpty()
                .WithMessage("Map at least one column before importing.");
            RuleFor(mapping => mapping.Columns).Must(columns => columns.Count <= 100)
                .WithMessage("An import can map at most 100 columns.");
        }
    }
}
