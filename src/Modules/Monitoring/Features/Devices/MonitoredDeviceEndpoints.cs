using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Monitoring.Features.Devices;

public static class MonitoredDeviceEndpoints
{
    public static IEndpointRouteBuilder MapMonitoredDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/api/monitored-devices").RequireAuthorization("CanManageMonitoring");

        devices.MapGet("/", async (string? search, Guid? ciId, string? pollerGroup, bool? isEnabled,
                int? page, int? pageSize, IMonitoredDeviceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(
                new MonitoredDeviceListRequest(search, ciId, pollerGroup, isEnabled, page ?? 1, pageSize ?? 25),
                cancellationToken)));

        devices.MapGet("/{id:guid}", async (Guid id, IMonitoredDeviceService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } device
                ? Results.Ok(device)
                : NotFound("Monitored device not found."));

        devices.MapPost("/", async (CreateMonitoredDeviceRequest request, ClaimsPrincipal user,
            IMonitoredDeviceService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateMonitoredDeviceValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success =>
                    Results.Created($"/api/monitored-devices/{result.Device!.Id}", result.Device),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.Duplicate => Conflict("CI is already monitored.", result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        devices.MapPut("/{id:guid}", async (Guid id, UpdateMonitoredDeviceRequest request, ClaimsPrincipal user,
            IMonitoredDeviceService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateMonitoredDeviceValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Device),
                MonitoringOutcome.NotFound => NotFound("Monitored device not found."),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        devices.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMonitoredDeviceService service,
                CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, user, cancellationToken) switch
            {
                MonitoringOutcome.Success => Results.NoContent(),
                MonitoringOutcome.NotFound => NotFound("Monitored device not found."),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            });

        devices.MapGet("/{id:guid}/checks", async (Guid id, IMonitoredDeviceService service,
                CancellationToken cancellationToken) =>
            await service.ListChecksAsync(id, cancellationToken) is { } checks
                ? Results.Ok(checks)
                : NotFound("Monitored device not found."));

        devices.MapPost("/{id:guid}/checks", async (Guid id, CreateCheckRequest request, ClaimsPrincipal user,
            IMonitoredDeviceService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateCheckValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateCheckAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Created($"/api/checks/{result.Check!.Id}", result.Check),
                MonitoringOutcome.NotFound => NotFound("Monitored device not found."),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.Duplicate => Conflict("Check name is already used.", result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        // A check is addressed on its own once it exists — its device is fixed, so nesting the id
        // twice would let a caller name a device the check does not belong to.
        var checks = endpoints.MapGroup("/api/checks").RequireAuthorization("CanManageMonitoring");

        checks.MapPut("/{checkId:guid}", async (Guid checkId, UpdateCheckRequest request, ClaimsPrincipal user,
            IMonitoredDeviceService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateCheckValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateCheckAsync(checkId, request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Check),
                MonitoringOutcome.NotFound => NotFound("Check not found."),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.Duplicate => Conflict("Check name is already used.", result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        checks.MapDelete("/{checkId:guid}", async (Guid checkId, ClaimsPrincipal user,
                IMonitoredDeviceService service, CancellationToken cancellationToken) =>
            await service.DeleteCheckAsync(checkId, user, cancellationToken) switch
            {
                MonitoringOutcome.Success => Results.NoContent(),
                MonitoringOutcome.NotFound => NotFound("Check not found."),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            });

        return endpoints;
    }

    private static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    private static IResult Conflict(string title, string? detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    private sealed class CreateMonitoredDeviceValidator : AbstractValidator<CreateMonitoredDeviceRequest>
    {
        public CreateMonitoredDeviceValidator()
        {
            RuleFor(request => request.CiId).NotEqual(Guid.Empty);
            RuleFor(request => request.Address).NotEmpty().MaximumLength(255);
            RuleFor(request => request.PollerGroup).MaximumLength(100);
            RuleFor(request => request.Notes).MaximumLength(2_000);
        }
    }

    private sealed class UpdateMonitoredDeviceValidator : AbstractValidator<UpdateMonitoredDeviceRequest>
    {
        public UpdateMonitoredDeviceValidator()
        {
            RuleFor(request => request.Address).NotEmpty().MaximumLength(255);
            RuleFor(request => request.PollerGroup).MaximumLength(100);
            RuleFor(request => request.Notes).MaximumLength(2_000);
        }
    }

    private sealed class CreateCheckValidator : AbstractValidator<CreateCheckRequest>
    {
        public CreateCheckValidator()
        {
            RuleFor(request => request.Type).IsInEnum();
            RuleFor(request => request.Comparison).IsInEnum();
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
        }
    }

    private sealed class UpdateCheckValidator : AbstractValidator<UpdateCheckRequest>
    {
        public UpdateCheckValidator()
        {
            RuleFor(request => request.Comparison).IsInEnum();
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
        }
    }
}
