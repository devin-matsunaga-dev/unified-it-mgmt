using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Assets.Features.Timeline;

public static class CiTimelineEndpoints
{
    public static IEndpointRouteBuilder MapCiTimelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Behind the CMDB's own policy, beside the CI reads it belongs to. It writes nothing — a history
        // is a reading of what already happened — and there is deliberately no POST counterpart: every
        // event on this axis is already produced by the endpoint that owns it.
        endpoints.MapGet("/api/cis/{id:guid}/timeline", async (
            Guid id,
            string? types,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            ICiTimelineService service,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

            // Unlike `maxDepth` on the blast radius, an unrecognised type is refused rather than ignored.
            // A clamped depth still answers the question that was asked; a filter the server silently
            // dropped answers a different one — "alerts only" spelled wrongly would return everything and
            // look like the filter is broken. The refusal names every kind, following the importer's rule.
            var kinds = new List<CiTimelineEventKind>();
            if (!string.IsNullOrWhiteSpace(types))
            {
                foreach (var token in types.Split(',', StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries))
                {
                    // `IsDefined` as well as `TryParse`, because `TryParse` happily accepts any integer:
                    // `?types=99` would otherwise parse to a kind that does not exist, match no source,
                    // and answer with an empty timeline and no complaint.
                    if (Enum.TryParse<CiTimelineEventKind>(token, ignoreCase: true, out var kind)
                        && Enum.IsDefined(kind))
                    {
                        kinds.Add(kind);
                    }
                    else
                    {
                        errors["types"] =
                        [
                            $"'{token}' is not a timeline event kind. Use {string.Join(", ",
                                CiTimelineAssembler.AllKinds)}.",
                        ];
                        break;
                    }
                }
            }

            // A window that ends before it starts is a typo rather than an empty timeline, and answering
            // it with nothing would be indistinguishable from an asset with no history.
            if (from is not null && to is not null && from > to)
            {
                errors["to"] = ["The end of the window must not be before its start."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            // Clamped rather than refused, following the traversal endpoints: asking for more history than
            // the cap allows is not a mistake, and the response echoes the limit it actually applied.
            var timeline = await service.GetTimelineAsync(
                id,
                new CiTimelineRequest(kinds, from, to, limit ?? CiTimelineService.DefaultLimit),
                cancellationToken);

            return timeline is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "CI not found.")
                : Results.Ok(timeline);
        }).RequireAuthorization("CanManageAssets");

        return endpoints;
    }
}
