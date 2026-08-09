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
        // The poller has no identity of its own until WP-3.2 issues it credentials, so both the
        // registration and the config fetch sit behind the operator policy for now. That is an
        // interim: a poller must not need an agent's rights to read its own configuration.
        var group = endpoints.MapGroup("/api/pollers").RequireAuthorization("CanManageMonitoring");

        group.MapGet("/", async (IPollerService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapPost("/registrations", async (RegisterPollerRequest request, ClaimsPrincipal user,
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
        group.MapGet("/{name}/config", async (string name, long? sinceVersion, IPollerService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetConfigAsync(name, sinceVersion, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Config),
                MonitoringOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Poller not found.",
                    detail: "Register the poller before fetching its configuration."),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        return endpoints;
    }

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
