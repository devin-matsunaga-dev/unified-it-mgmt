using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.Discovery;

public static class ScanProfileEndpoints
{
    public static IEndpointRouteBuilder MapScanProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Two audiences, two policies, the same split WP-3.2 made for the poller: managing what gets
        // scanned is an operator's job, and reading the list is the scanner's. `CanDiscover` is the
        // scanner's own credential and nothing else — a scanner must not need an agent's rights, and
        // an agent has no business fetching a scanner's work list.
        var profiles = endpoints.MapGroup("/api/scan-profiles")
            .RequireAuthorization("CanManageMonitoring");
        var discovery = endpoints.MapGroup("/api/discovery").RequireAuthorization("CanDiscover");

        profiles.MapGet("/", async (string? search, string? discoveryGroup, bool? isEnabled,
                int? page, int? pageSize, IScanProfileService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(
                new ScanProfileListRequest(search, discoveryGroup, isEnabled, page ?? 1, pageSize ?? 25),
                cancellationToken)));

        profiles.MapGet("/{id:guid}", async (Guid id, IScanProfileService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } profile
                ? Results.Ok(profile)
                : NotFound());

        profiles.MapPost("/", async (CreateScanProfileRequest request, ClaimsPrincipal user,
            IScanProfileService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateScanProfileValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success =>
                    Results.Created($"/api/scan-profiles/{result.Profile!.Id}", result.Profile),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.Duplicate => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        profiles.MapPut("/{id:guid}", async (Guid id, UpdateScanProfileRequest request, ClaimsPrincipal user,
            IScanProfileService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateScanProfileValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Profile),
                MonitoringOutcome.NotFound => NotFound(),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.Duplicate => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        profiles.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IScanProfileService service,
                CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, user, cancellationToken) switch
            {
                MonitoringOutcome.Success => Results.NoContent(),
                MonitoringOutcome.NotFound => NotFound(),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            });

        // The scanner's own read. Keyed by group rather than by a scanner name because, unlike a
        // poller, a discovery service has nothing registered about it: it holds no configuration
        // version and reports no heartbeat, so a name in the URL would be a string the server has
        // never seen and could not answer differently for.
        discovery.MapGet("/{discoveryGroup}/scan-profiles", async (string discoveryGroup,
                IScanProfileService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetConfigAsync(discoveryGroup, cancellationToken)));

        return endpoints;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Scan profile not found.");

    private static IResult Conflict(string? detail) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Scan profile name is already used.",
        detail: detail);

    private sealed class CreateScanProfileValidator : AbstractValidator<CreateScanProfileRequest>
    {
        public CreateScanProfileValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(2_000);
            RuleFor(request => request.DiscoveryGroup).MaximumLength(100);
        }
    }

    private sealed class UpdateScanProfileValidator : AbstractValidator<UpdateScanProfileRequest>
    {
        public UpdateScanProfileValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(2_000);
            RuleFor(request => request.DiscoveryGroup).MaximumLength(100);
        }
    }
}
