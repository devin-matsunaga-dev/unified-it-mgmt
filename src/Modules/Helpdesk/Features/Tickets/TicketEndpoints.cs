using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Helpdesk.Data;

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

            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                TicketWriteOutcome.Success => Results.Created($"/api/tickets/{result.Ticket!.Id}", result.Ticket),
                TicketWriteOutcome.QueueNotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Queue not found."),
                TicketWriteOutcome.CategoryNotFound => Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        [nameof(request.CategoryId)] = ["Category not found or inactive."],
                    }),
                TicketWriteOutcome.InvalidCustomFields => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown ticket write outcome '{outcome}'."),
            };
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
            string? q,
            string[]? status,
            string[]? priority,
            string? type,
            Guid? queueId,
            string? assignee,
            Guid? categoryId,
            bool? unassigned,
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

            var priorities = new List<TicketPriority>();
            foreach (var value in priority ?? [])
            {
                if (Enum.TryParse<TicketPriority>(value, ignoreCase: true, out var parsed))
                {
                    priorities.Add(parsed);
                }
                else
                {
                    errors[nameof(priority)] = [$"Priority '{value}' does not exist."];
                }
            }

            TicketType? ticketType = null;
            if (!string.IsNullOrWhiteSpace(type))
            {
                if (Enum.TryParse<TicketType>(type, ignoreCase: true, out var parsedType))
                {
                    ticketType = parsedType;
                }
                else
                {
                    errors[nameof(type)] = [$"Ticket type '{type}' does not exist."];
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var filter = new TicketListFilter(
                q, status, priorities, ticketType, queueId, assignee, categoryId, unassigned ?? false);
            var result = await service.ListAsync(filter, resolvedPage, resolvedPageSize, user, cancellationToken);
            return result.Errors is null ? Results.Ok(result.Page) : Results.ValidationProblem(result.Errors);
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

            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                TicketWriteOutcome.Success => Results.Ok(result.Ticket),
                TicketWriteOutcome.TicketNotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Ticket not found."),
                TicketWriteOutcome.CategoryNotFound => Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        [nameof(request.CategoryId)] = ["Category not found or inactive."],
                    }),
                TicketWriteOutcome.InvalidCustomFields => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown ticket write outcome '{outcome}'."),
            };
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
                TransitionTicketOutcome.Forbidden => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Ticket transition is not allowed.",
                    detail: result.Error),
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
