using System.Security.Claims;

using FluentValidation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Topology;

public static class TopologyEndpoints
{
    public static IEndpointRouteBuilder MapTopologyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A CMDB read and a CMDB write, so `CanManageAssets` on both halves. The live colouring the map
        // draws over this comes from the monitoring status board on its own policy — a person who may
        // see the estate's shape but not its alerts gets an uncoloured map rather than a 403.
        endpoints.MapGet("/api/topology", async (string? types, bool? includeIsolated,
            ITopologyService service, CancellationToken cancellationToken) =>
        {
            if (!TryParseTypes(types, out var parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["types"] = [$"'{types}' contains a value that is not a CI type."],
                });
            }

            return Results.Ok(await service.GetAsync(
                new TopologyRequest(parsed, includeIsolated ?? false), cancellationToken));
        }).RequireAuthorization("CanManageAssets");

        var maps = endpoints.MapGroup("/api/topology-maps").RequireAuthorization("CanManageAssets");

        maps.MapGet("/", async (ITopologyMapService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        maps.MapGet("/{id:guid}", async (Guid id, ITopologyMapService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } map ? Results.Ok(map) : NotFound());

        maps.MapPost("/", async (SaveTopologyMapRequest request, ClaimsPrincipal user,
            ITopologyMapService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                TopologyMapOutcome.Success =>
                    Results.Created($"/api/topology-maps/{result.Map!.Id}", result.Map),
                TopologyMapOutcome.DuplicateName => Conflict(result.Error),
                TopologyMapOutcome.UnknownCi => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown topology map outcome '{outcome}'."),
            };
        });

        maps.MapPut("/{id:guid}", async (Guid id, SaveTopologyMapRequest request, ClaimsPrincipal user,
            ITopologyMapService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                TopologyMapOutcome.Success => Results.Ok(result.Map),
                TopologyMapOutcome.NotFound => NotFound(),
                TopologyMapOutcome.DuplicateName => Conflict(result.Error),
                TopologyMapOutcome.UnknownCi => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown topology map outcome '{outcome}'."),
            };
        });

        maps.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ITopologyMapService service,
                CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, user, cancellationToken) switch
            {
                TopologyMapOutcome.Success => Results.NoContent(),
                TopologyMapOutcome.NotFound => NotFound(),
                var outcome => throw new InvalidOperationException($"Unknown topology map outcome '{outcome}'."),
            });

        return endpoints;
    }

    private static bool TryParseTypes(string? types, out IReadOnlyList<CiType>? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(types))
        {
            return true;
        }

        var values = new List<CiType>();
        foreach (var token in types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<CiType>(token, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                return false;
            }

            values.Add(value);
        }

        parsed = values;
        return true;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Topology map not found.");

    private static IResult Conflict(string? detail) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "That map name is taken.", detail: detail);

    private sealed class SaveValidator : AbstractValidator<SaveTopologyMapRequest>
    {
        /// <summary>
        /// The canvas is unbounded but not infinite: a coordinate that is not a finite number would
        /// render the map blank with nothing to say why, and it can only arrive from a broken client.
        /// </summary>
        public SaveValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(1_000);
            RuleFor(request => request.Nodes).NotNull();
            RuleForEach(request => request.Nodes).ChildRules(node =>
            {
                node.RuleFor(item => item.CiId).NotEqual(Guid.Empty);
                node.RuleFor(item => item.X).Must(double.IsFinite).WithMessage("'X' must be a finite number.");
                node.RuleFor(item => item.Y).Must(double.IsFinite).WithMessage("'Y' must be a finite number.");
            });
        }
    }
}
