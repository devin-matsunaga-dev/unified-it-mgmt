using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Dashboards;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// The alert board's read surface, plus its one write. WP-3.5 and WP-3.7 both deliberately deferred
/// this here rather than guess at its shape a package early.
/// </summary>
public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var alerts = endpoints.MapGroup("/api/alerts").RequireAuthorization("CanManageMonitoring");

        alerts.MapGet("/", async (
                AlertStatus? status,
                AlertSeverity? severity,
                Guid? deviceId,
                Guid? ciId,
                bool? acknowledged,
                int? page,
                int? pageSize,
                IAlertService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(
                new AlertListRequest(
                    // No `status` means the board's question — what is wrong now. Cleared alerts are
                    // history and have to be asked for by name.
                    status ?? AlertStatus.Open,
                    severity,
                    deviceId,
                    ciId,
                    acknowledged,
                    page ?? 1,
                    pageSize ?? 25),
                cancellationToken)));

        alerts.MapGet("/{id:guid}", async (Guid id, IAlertService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } alert
                ? Results.Ok(alert)
                : NotFound("Alert not found."));

        // POST an acknowledgement rather than PUT a flag: it is an event somebody performed, and the
        // resource is the acknowledgement, following the `/api/tickets/{id}/transitions` precedent.
        alerts.MapPost("/{id:guid}/acknowledgements", async (
            Guid id,
            ClaimsPrincipal user,
            IAlertService service,
            IMonitoringLiveUpdateService liveUpdates,
            CancellationToken cancellationToken) =>
        {
            var result = await service.AcknowledgeAsync(id, user, cancellationToken);
            if (result.Outcome is AlertActionOutcome.Success)
            {
                // "Ack reflects everywhere" is a WP verification step: every other board watching this
                // alert learns about it from the same push an alert change uses.
                await liveUpdates.PublishAlertChangeAsync(id, cancellationToken);
            }

            return result.Outcome switch
            {
                AlertActionOutcome.Success => Results.Ok(result.Alert),
                AlertActionOutcome.NotFound => NotFound("Alert not found."),
                AlertActionOutcome.Conflict => Conflict("Alert cannot be acknowledged.", result.Error),
                var outcome => throw new InvalidOperationException($"Unknown alert outcome '{outcome}'."),
            };
        });

        return endpoints;
    }

    private static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    private static IResult Conflict(string title, string? detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}
