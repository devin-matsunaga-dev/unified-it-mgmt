using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.MaintenanceWindows;

public static class MaintenanceWindowEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceWindowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/maintenance-windows").RequireAuthorization("CanManageMonitoring");

        group.MapGet("/", async (string? search, Guid? deviceId, bool? isActive, MaintenanceWindowStatus? status,
                int? page, int? pageSize, IMaintenanceWindowService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(
                new MaintenanceWindowListRequest(search, deviceId, isActive, status, page ?? 1, pageSize ?? 25),
                cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, IMaintenanceWindowService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } window
                ? Results.Ok(window)
                : NotFound());

        group.MapPost("/", async (CreateMaintenanceWindowRequest request, ClaimsPrincipal user,
            IMaintenanceWindowService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateMaintenanceWindowValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success =>
                    Results.Created($"/api/maintenance-windows/{result.Window!.Id}", result.Window),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateMaintenanceWindowRequest request, ClaimsPrincipal user,
            IMaintenanceWindowService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateMaintenanceWindowValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Window),
                MonitoringOutcome.NotFound => NotFound(),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMaintenanceWindowService service,
                CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, user, cancellationToken) switch
            {
                MonitoringOutcome.Success => Results.NoContent(),
                MonitoringOutcome.NotFound => NotFound(),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            });

        return endpoints;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Maintenance window not found.");

    private sealed class CreateMaintenanceWindowValidator : AbstractValidator<CreateMaintenanceWindowRequest>
    {
        public CreateMaintenanceWindowValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(2_000);
        }
    }

    private sealed class UpdateMaintenanceWindowValidator : AbstractValidator<UpdateMaintenanceWindowRequest>
    {
        public UpdateMaintenanceWindowValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(2_000);
        }
    }
}
