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
        admin.MapPost("/policies", async (CreateSlaPolicyRequest request, ClaimsPrincipal user,
            ISlaService service, CancellationToken cancellationToken) =>
        {
            var validation = await new PolicyValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreatePolicyAsync(request, user, cancellationToken);
            return result is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Business-hours calendar not found.")
                : Results.Created($"/api/sla/policies/{result.Id}", result);
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

    private sealed class PolicyValidator : AbstractValidator<CreateSlaPolicyRequest>
    {
        public PolicyValidator()
        {
            RuleFor(item => item.Name).NotEmpty().MaximumLength(100);
            RuleFor(item => item.Priority).IsInEnum();
            RuleFor(item => item.Category).MaximumLength(100);
            RuleFor(item => item.ResponseTargetMinutes).InclusiveBetween(1, 525_600);
            RuleFor(item => item.ResolutionTargetMinutes).InclusiveBetween(1, 525_600)
                .GreaterThanOrEqualTo(item => item.ResponseTargetMinutes);
            RuleFor(item => item.WarningPercent).InclusiveBetween(1, 99);
            RuleFor(item => item.CalendarId).NotEmpty();
        }
    }
}
