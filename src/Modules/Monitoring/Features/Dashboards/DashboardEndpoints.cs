using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Monitoring.Features.Dashboards;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var monitoring = endpoints.MapGroup("/api/monitoring").RequireAuthorization("CanManageMonitoring");

        monitoring.MapGet("/status-board", async (
                string? search,
                string? pollerGroup,
                int? page,
                int? pageSize,
                IStatusBoardService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(
                new StatusBoardRequest(search, pollerGroup, page ?? 1, pageSize ?? 25),
                cancellationToken)));

        monitoring.MapGet("/status-board/{deviceId:guid}", async (
                Guid deviceId,
                IStatusBoardService service,
                CancellationToken cancellationToken) =>
            await service.GetTileAsync(deviceId, cancellationToken) is { } tile
                ? Results.Ok(tile)
                : Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Monitored device not found."));

        return endpoints;
    }
}
