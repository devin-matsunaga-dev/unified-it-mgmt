using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.PollerConfig;

public static class PollerEndpoints
{
    public static IEndpointRouteBuilder MapPollerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Two audiences, two policies. The list is an operator's view of the fleet; registration and
        // the config fetch are the poller talking about itself, and WP-3.2 gave it a credential of
        // its own so it no longer borrows an agent's.
        var group = endpoints.MapGroup("/api/pollers").RequireAuthorization("CanManageMonitoring");
        var pollerGroup = endpoints.MapGroup("/api/pollers").RequireAuthorization("CanPoll");

        group.MapGet("/", async (IPollerService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        pollerGroup.MapPost("/registrations", async (RegisterPollerRequest request, ClaimsPrincipal user,
            IPollerService service, CancellationToken cancellationToken) =>
        {
            var validation = await new RegisterPollerValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.RegisterAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Poller),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        // Omitting sinceVersion asks for a full snapshot; passing the version the poller holds asks
        // for what has changed since, which is what keeps a steady-state cycle nearly empty.
        pollerGroup.MapGet("/{name}/config", async (string name, long? sinceVersion, IPollerService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetConfigAsync(name, sinceVersion, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Config),
                MonitoringOutcome.NotFound => PollerNotFound(),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        // WP-3.11. Two calls rather than one, and the split is the point: the scope is free to ask for
        // every cycle because it writes nothing and carries no secret, while the grant writes a row
        // and is asked for only when a version the poller holds has moved. The poller never names a
        // credential in either request — the scope is derived from its own group's devices.
        pollerGroup.MapGet("/{name}/credentials", async (string name, IPollerCredentialService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetScopeAsync(name, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Scope),
                MonitoringOutcome.NotFound => PollerNotFound(),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        pollerGroup.MapPost("/{name}/credential-grants", async (string name, ClaimsPrincipal user,
            IPollerCredentialService service, CancellationToken cancellationToken) =>
        {
            var result = await service.IssueGrantAsync(name, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Grant),
                MonitoringOutcome.NotFound => PollerNotFound(),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        return endpoints;
    }

    private static IResult PollerNotFound() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Poller not found.",
        detail: "Register the poller before fetching its configuration.");

    private sealed class RegisterPollerValidator : AbstractValidator<RegisterPollerRequest>
    {
        public RegisterPollerValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
            RuleFor(request => request.PollerGroup).MaximumLength(100);
            RuleFor(request => request.AgentVersion).MaximumLength(50);
        }
    }
}
