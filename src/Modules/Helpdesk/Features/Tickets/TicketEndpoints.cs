using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Helpdesk.Features.Tickets;

public static class TicketEndpoints
{
    private const string TicketPolicy = "CanManageTickets";

    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tickets").RequireAuthorization(TicketPolicy);

        group.MapPost("/", async (
            CreateTicketRequest request,
            ClaimsPrincipal user,
            ITicketService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new CreateTicketRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var ticket = await service.CreateAsync(request, user, cancellationToken);
            return ticket is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Queue not found.")
                : Results.Created($"/api/tickets/{ticket.Id}", ticket);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            ITicketService service,
            CancellationToken cancellationToken) =>
        {
            var ticket = await service.GetAsync(id, user, cancellationToken);
            return ticket is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found.")
                : Results.Ok(ticket);
        });

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            ClaimsPrincipal user,
            ITicketService service,
            CancellationToken cancellationToken) =>
        {
            var resolvedPage = page ?? 1;
            var resolvedPageSize = pageSize ?? 25;
            var errors = new Dictionary<string, string[]>();
            if (resolvedPage < 1)
            {
                errors[nameof(page)] = ["Page must be at least 1."];
            }

            if (resolvedPageSize is < 1 or > 200)
            {
                errors[nameof(pageSize)] = ["Page size must be between 1 and 200."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            return Results.Ok(await service.ListAsync(
                resolvedPage, resolvedPageSize, user, cancellationToken));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateTicketRequest request,
            ClaimsPrincipal user,
            ITicketService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateTicketRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var ticket = await service.UpdateAsync(id, request, user, cancellationToken);
            return ticket is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found.")
                : Results.Ok(ticket);
        });

        group.MapPost("/{id:guid}/transitions", async (
            Guid id,
            TransitionTicketRequest request,
            ClaimsPrincipal user,
            ITicketService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new TransitionTicketRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await service.TransitionAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                TransitionTicketOutcome.Success => Results.Ok(result.Ticket),
                TransitionTicketOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Ticket not found."),
                TransitionTicketOutcome.UnknownStatus => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.TargetStatus)] = [result.Error!] }),
                TransitionTicketOutcome.ResolutionNoteRequired => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.ResolutionNote)] = [result.Error!] }),
                TransitionTicketOutcome.IllegalTransition => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Ticket transition is not allowed.",
                    detail: result.Error),
                _ => throw new InvalidOperationException($"Unknown transition outcome '{result.Outcome}'."),
            };
        });

        group.MapGet("/{id:guid}/transitions", async (
            Guid id,
            ClaimsPrincipal user,
            ITicketService service,
            CancellationToken cancellationToken) =>
        {
            var history = await service.GetTransitionHistoryAsync(id, user, cancellationToken);
            return history is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ticket not found.")
                : Results.Ok(history);
        });

        return endpoints;
    }
}
