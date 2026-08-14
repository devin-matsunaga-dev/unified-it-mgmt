using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Assets.Features.Drift;

public static class DriftEndpoints
{
    public static IEndpointRouteBuilder MapDriftEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A read of the CMDB beside a read of what discovery observed, so the CMDB's own policy. It
        // writes nothing at all, which is why there is no counterpart POST here: every correction the
        // report suggests is made through the CI's own endpoint, audited as the edit it is.
        endpoints.MapGet("/api/drift", async (string? kind, string? field, Guid? siteId, int? staleAfterDays,
            int? page, int? pageSize, IDriftService service, CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

            DriftFindingKind? parsedKind = null;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                if (Enum.TryParse<DriftFindingKind>(kind, ignoreCase: true, out var value))
                {
                    parsedKind = value;
                }
                else
                {
                    errors["kind"] = [$"'{kind}' is not a drift finding kind. Use New, Missing, or Changed."];
                }
            }

            if (!string.IsNullOrWhiteSpace(field)
                && !DriftFields.All.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                errors["field"] =
                    [$"'{field}' is not a compared field. Use {string.Join(", ", DriftFields.All)}."];
            }

            // Zero would mean "everything is stale", which reads as a broken report rather than as a
            // filter nobody meant to set.
            if (staleAfterDays is < 1 or > 3_650)
            {
                errors["staleAfterDays"] = ["Days without a sighting must be between 1 and 3650."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            return Results.Ok(await service.GetAsync(
                new DriftReportRequest(
                    parsedKind,
                    string.IsNullOrWhiteSpace(field) ? null : field.Trim(),
                    siteId,
                    staleAfterDays,
                    page ?? 1,
                    pageSize ?? 25),
                cancellationToken));
        }).RequireAuthorization("CanManageAssets");

        return endpoints;
    }
}
