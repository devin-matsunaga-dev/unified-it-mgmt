using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Helpdesk.Features.Assignments;

public static class AssignmentEndpoints
{
    public static IEndpointRouteBuilder MapAssignmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api").RequireAuthorization("CanManageTickets");

        group.MapPost("/teams", async (CreateTeamRequest request, ClaimsPrincipal user,
            IAssignmentService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateTeamRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            return Results.Created("/api/teams", await service.CreateTeamAsync(request, user, cancellationToken));
        });

        group.MapPost("/teams/{teamId:guid}/members", async (Guid teamId, AddTeamMemberRequest request,
            ClaimsPrincipal user, IAssignmentService service, CancellationToken cancellationToken) =>
        {
            var validation = await new AddTeamMemberRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            return await service.AddTeamMemberAsync(teamId, request, user, cancellationToken)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Team not found.");
        });

        group.MapPost("/queues", async (CreateQueueRequest request, ClaimsPrincipal user,
            IAssignmentService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateQueueRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var queue = await service.CreateQueueAsync(request, user, cancellationToken);
            return queue is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Team not found.")
                : Results.Created($"/api/queues/{queue.Id}", queue);
        });

        group.MapGet("/queues", async (IAssignmentService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListQueuesAsync(cancellationToken)));

        group.MapPost("/tickets/{ticketId:guid}/queue", async (Guid ticketId, PlaceTicketInQueueRequest request,
            ClaimsPrincipal user, IAssignmentService service, CancellationToken cancellationToken) =>
        {
            var validation = await new PlaceTicketInQueueRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.PlaceInQueueAsync(ticketId, request, user, cancellationToken);
            return result.Outcome switch
            {
                QueuePlacementOutcome.Success => Results.Ok(result.Ticket),
                QueuePlacementOutcome.TicketNotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Ticket not found."),
                QueuePlacementOutcome.QueueNotFound => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.QueueId)] = ["Queue not found."] }),
                _ => throw new InvalidOperationException($"Unknown queue placement outcome '{result.Outcome}'."),
            };
        });

        group.MapPost("/tickets/{ticketId:guid}/assignments", async (Guid ticketId, AssignTicketRequest request,
            ClaimsPrincipal user, IAssignmentService service, CancellationToken cancellationToken) =>
        {
            var validation = await new AssignTicketRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.AssignAsync(ticketId, request, user, cancellationToken);
            return result.Outcome switch
            {
                AssignmentOutcome.Success => Results.Ok(result.Ticket),
                AssignmentOutcome.TicketNotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Ticket not found."),
                AssignmentOutcome.TicketHasNoQueue => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Ticket has no queue.", detail: result.Error),
                AssignmentOutcome.TechnicianNotInQueueTeam => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.TechnicianId)] = [result.Error!] }),
                _ => throw new InvalidOperationException($"Unknown assignment outcome '{result.Outcome}'."),
            };
        });

        group.MapGet("/tickets/{ticketId:guid}/assignments", async (Guid ticketId, ClaimsPrincipal user,
            IAssignmentService service, CancellationToken cancellationToken) =>
        {
            var history = await service.GetHistoryAsync(ticketId, user, cancellationToken);
            return history is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found.")
                : Results.Ok(history);
        });

        group.MapGet("/tickets/{ticketId:guid}/eligible-technicians", async (Guid ticketId, ClaimsPrincipal user,
            IAssignmentService service, CancellationToken cancellationToken) =>
        {
            var technicians = await service.GetEligibleTechniciansAsync(ticketId, user, cancellationToken);
            return technicians is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found.")
                : Results.Ok(technicians);
        });

        group.MapGet("/tickets/mine", async (int? page, int? pageSize, ClaimsPrincipal user,
            IAssignmentService service, CancellationToken cancellationToken) =>
        {
            var resolvedPage = page ?? 1;
            var resolvedPageSize = pageSize ?? 25;
            var errors = PaginationErrors(resolvedPage, resolvedPageSize);
            return errors.Count > 0
                ? Results.ValidationProblem(errors)
                : Results.Ok(await service.ListMineAsync(resolvedPage, resolvedPageSize, user, cancellationToken));
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> PaginationErrors(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1) errors[nameof(page)] = ["Page must be at least 1."];
        if (pageSize is < 1 or > 200) errors[nameof(pageSize)] = ["Page size must be between 1 and 200."];
        return errors;
    }
}
