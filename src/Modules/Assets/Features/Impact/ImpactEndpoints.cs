using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Assets.Features.Relationships;

namespace Modules.Assets.Features.Impact;

public static class ImpactEndpoints
{
    public static IEndpointRouteBuilder MapImpactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A read of the CMDB's own graph, so the CMDB's own policy — which the monitoring roles already
        // satisfy, so the alert board can mount the same panel without a second endpoint. There is no
        // POST counterpart here on purpose: a blast radius is a reading of what an outage would cost and
        // never a decision about it.
        endpoints.MapGet("/api/cis/{id:guid}/impact", async (Guid id, int? maxDepth, IImpactService service,
            CancellationToken cancellationToken) =>
        {
            // Out-of-range depths are clamped rather than refused, following the traversal endpoints
            // this one sits beside: asking how far a blast radius goes is not a mistake worth a 400.
            var impact = await service.GetImpactAsync(
                id, maxDepth ?? CiGraphQuery.DefaultDepth, cancellationToken);
            return impact is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "CI not found.")
                : Results.Ok(impact);
        }).RequireAuthorization("CanManageAssets");

        return endpoints;
    }
}
