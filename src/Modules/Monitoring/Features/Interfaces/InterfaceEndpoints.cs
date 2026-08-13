using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Monitoring.Features.Interfaces;

/// <summary>
/// The read side of interface monitoring. There is no write side, and that is the point: an
/// interface is discovered by polling the device, so there is nothing here to create, edit or
/// delete. What an operator configures is the check that finds them.
/// </summary>
public static class InterfaceEndpoints
{
    public static IEndpointRouteBuilder MapInterfaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/api/monitored-devices").RequireAuthorization("CanManageMonitoring");

        devices.MapGet("/{id:guid}/interfaces", async (Guid id, IInterfaceService service,
                CancellationToken cancellationToken) =>
            await service.ListAsync(id, cancellationToken) is { } interfaces
                ? Results.Ok(interfaces)
                : Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Monitored device not found."));

        return endpoints;
    }
}
