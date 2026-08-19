using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.Discovery;

/// <summary>
/// On-demand scanning, in the shape ARCHITECTURE §4 allows: an operator writes a request, a scanner
/// collects it. The two halves sit behind different policies and are disjoint in both directions — an
/// operator cannot collect a run and a scanner cannot request one.
/// </summary>
public static class ScanRunEndpoints
{
    public static IEndpointRouteBuilder MapScanRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var profiles = endpoints.MapGroup("/api/scan-profiles")
            .RequireAuthorization("CanManageMonitoring");
        var runs = endpoints.MapGroup("/api/scan-runs")
            .RequireAuthorization("CanManageMonitoring");
        var settings = endpoints.MapGroup("/api/discovery-settings")
            .RequireAuthorization("CanManageMonitoring");
        var agent = endpoints.MapGroup("/api/discovery").RequireAuthorization("CanDiscover");

        // ---- the operator channel ----

        profiles.MapPost("/{id:guid}/runs", async (Guid id, RequestScanRunRequest? request,
            ClaimsPrincipal user, IScanRunService service, CancellationToken cancellationToken) =>
        {
            var body = request ?? new RequestScanRunRequest();
            var validation = await new RequestScanRunValidator().ValidateAsync(body, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());

            var result = await service.RequestAsync(id, body, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Accepted($"/api/scan-runs/{result.Run!.Id}", result.Run),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Scan profile not found."),
                // A double press. ProblemDetails like every other non-2xx in this solution, and the
                // detail names the scan that is already coming — the honest answer to "why did my
                // button do nothing" is that the scan it asked for is already on its way.
                MonitoringOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "A scan of this profile is already queued.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        runs.MapGet("/", async (Guid? scanProfileId, string? status, int? page, int? pageSize,
                IScanRunService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(
                new ScanRunListRequest(scanProfileId, status, page ?? 1, pageSize ?? 25),
                cancellationToken)));

        runs.MapGet("/{id:guid}", async (Guid id, IScanRunService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } run
                ? Results.Ok(run)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Scan run not found."));

        settings.MapGet("/", async (IDiscoverySettingsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(cancellationToken)));

        settings.MapPut("/", async (UpdateDiscoverySettingsRequest request, ClaimsPrincipal user,
                IDiscoverySettingsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.UpdateAsync(request, user, cancellationToken)));

        // ---- the scanner channel ----

        // Keyed by group and carrying the scanner's name as a query rather than in the path, matching
        // the profile fetch beside it: a discovery group is the only thing the platform knows about a
        // scanner, and the name is a label for the row rather than an identity to check. WP-4.1
        // recorded that the poller's name-in-URL is not verified either; nothing here makes that worse,
        // because every scanner in a group is meant to be able to run any of the group's work.
        agent.MapGet("/{discoveryGroup}/scan-runs", async (string discoveryGroup, string? discoveryName,
            IScanDispatchService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ClaimAsync(
                discoveryGroup, string.IsNullOrWhiteSpace(discoveryName) ? "unnamed" : discoveryName.Trim(),
                cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Dispatch),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        // Progress, posted repeatedly while a sweep is in flight. Separate from the result endpoint so
        // that "how far have you got" can never be mistaken for "here is the outcome" — this one moves
        // no status and can never finish a run.
        agent.MapPost("/{discoveryGroup}/scan-runs/{id:guid}/progress", async (string discoveryGroup,
            Guid id, ReportScanProgressRequest request, IScanDispatchService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ReportProgressAsync(discoveryGroup, id, request, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Run),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Scan run not found.",
                    detail: "It does not exist, or it is not one this discovery group holds."),
                MonitoringOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Scan run is no longer collecting progress.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        agent.MapPost("/{discoveryGroup}/scan-runs/{id:guid}/results", async (string discoveryGroup,
            Guid id, ReportScanRunRequest request, IScanDispatchService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ReportAsync(discoveryGroup, id, request, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Run),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Scan run not found.",
                    detail: "It does not exist, or it is not one this discovery group holds."),
                MonitoringOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Scan run is already finished.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        return endpoints;
    }

    private sealed class RequestScanRunValidator : AbstractValidator<RequestScanRunRequest>
    {
        public RequestScanRunValidator() => RuleFor(request => request.Note).MaximumLength(500);
    }
}
