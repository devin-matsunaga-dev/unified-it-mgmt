using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Helpdesk.Features.Sla;

public static class SlaEndpoints
{
    public static IEndpointRouteBuilder MapSlaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/sla").RequireAuthorization("AdminOnly");
        admin.MapPost("/calendars", async (CreateBusinessHoursCalendarRequest request, ClaimsPrincipal user,
            ISlaService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CalendarValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            try
            {
                var result = await service.CreateCalendarAsync(request, user, cancellationToken);
                return Results.Created($"/api/sla/calendars/{result.Id}", result);
            }
            catch (TimeZoneNotFoundException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.TimeZoneId)] = ["Time zone is not supported."] });
            }
            catch (InvalidTimeZoneException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.TimeZoneId)] = ["Time zone is invalid."] });
            }
        });
        admin.MapGet("/calendars", async (ISlaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListCalendarsAsync(cancellationToken)));

        admin.MapDelete("/calendars/{id:guid}", async (Guid id, ClaimsPrincipal user, ISlaService service,
            CancellationToken cancellationToken) =>
            await service.DeleteCalendarAsync(id, user, cancellationToken) switch
            {
                SlaOutcome.Success => Results.NoContent(),
                SlaOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Business-hours calendar not found."),
                SlaOutcome.InUse => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Calendar is in use.",
                    detail: "Policies or running tickets still measure against it."),
                var outcome => throw new InvalidOperationException($"Unknown SLA outcome '{outcome}'."),
            });

        admin.MapGet("/policies", async (ISlaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListPoliciesAsync(cancellationToken)));

        admin.MapPost("/policies", async (SavePolicyRequest request, ClaimsPrincipal user,
            ISlaService service, CancellationToken cancellationToken) =>
        {
            var validation = await new PolicyValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreatePolicyAsync(request, user, cancellationToken);
            return PolicyResult(result, created: true);
        });

        admin.MapPut("/policies/{id:guid}", async (Guid id, SavePolicyRequest request, ClaimsPrincipal user,
            ISlaService service, CancellationToken cancellationToken) =>
        {
            var validation = await new PolicyValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdatePolicyAsync(id, request, user, cancellationToken);
            return PolicyResult(result, created: false);
        });

        admin.MapDelete("/policies/{id:guid}", async (Guid id, ClaimsPrincipal user, ISlaService service,
            CancellationToken cancellationToken) =>
            await service.DeletePolicyAsync(id, user, cancellationToken) switch
            {
                SlaOutcome.Success => Results.NoContent(),
                SlaOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "SLA policy not found."),
                SlaOutcome.InUse => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Policy is in use.",
                    detail: "Tickets have already run against it; deactivate it instead so their clocks stay explainable."),
                var outcome => throw new InvalidOperationException($"Unknown SLA outcome '{outcome}'."),
            });

        admin.MapPost("/policies/order", async (ReorderPoliciesRequest request, ClaimsPrincipal user,
            ISlaService service, CancellationToken cancellationToken) =>
        {
            await service.ReorderPoliciesAsync(request.PolicyIds, user, cancellationToken);
            return Results.Ok(await service.ListPoliciesAsync(cancellationToken));
        });

        endpoints.MapGet("/api/tickets/{ticketId:guid}/sla", async (Guid ticketId, ClaimsPrincipal user,
            ISlaService service, CancellationToken cancellationToken) =>
        {
            var result = await service.GetRemainingAsync(ticketId, user, cancellationToken);
            return result is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket SLA not found.")
                : Results.Ok(result);
        }).RequireAuthorization("CanManageTickets");
        return endpoints;
    }

    private sealed class CalendarValidator : AbstractValidator<CreateBusinessHoursCalendarRequest>
    {
        public CalendarValidator()
        {
            RuleFor(item => item.Name).NotEmpty().MaximumLength(100);
            RuleFor(item => item.TimeZoneId).NotEmpty().MaximumLength(100);
            RuleFor(item => item.WorkingDays).NotEqual(Data.BusinessDays.None);
            RuleFor(item => item.EndTime).GreaterThan(item => item.StartTime);
        }
    }

    /// <summary>Created and updated answer the same way; only the status differs.</summary>
    private static IResult PolicyResult(SlaPolicyResult result, bool created) => result.Outcome switch
    {
        SlaOutcome.Success when created =>
            Results.Created($"/api/sla/policies/{result.Policy!.Id}", result.Policy),
        SlaOutcome.Success => Results.Ok(result.Policy),
        SlaOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound, title: "SLA policy not found."),
        SlaOutcome.CalendarNotFound => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["calendarId"] = ["Business-hours calendar not found."] }),
        SlaOutcome.CategoryNotFound => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["categoryId"] = ["Ticket category not found."] }),
        var outcome => throw new InvalidOperationException($"Unknown SLA outcome '{outcome}'."),
    };

    private sealed class PolicyValidator : AbstractValidator<SavePolicyRequest>
    {
        public PolicyValidator()
        {
            RuleFor(item => item.Name).NotEmpty().MaximumLength(100);
            // Every condition is optional; null means the condition is simply not applied.
            RuleFor(item => item.Priority).IsInEnum().When(item => item.Priority is not null);
            RuleFor(item => item.TicketType).IsInEnum().When(item => item.TicketType is not null);
            RuleFor(item => item.SortOrder).InclusiveBetween(0, 10_000);
            RuleFor(item => item.ResponseTargetMinutes).InclusiveBetween(1, 525_600);
            RuleFor(item => item.ResolutionTargetMinutes).InclusiveBetween(1, 525_600)
                .GreaterThanOrEqualTo(item => item.ResponseTargetMinutes);
            RuleFor(item => item.WarningPercent).InclusiveBetween(1, 99);
            RuleFor(item => item.CalendarId).NotEmpty();
        }
    }
}
