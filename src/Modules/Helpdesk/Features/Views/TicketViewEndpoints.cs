using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Helpdesk.Features.Views;

public static class TicketViewEndpoints
{
    private const string TicketPolicy = "CanManageTickets";

    public static IEndpointRouteBuilder MapTicketViewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ticket-views").RequireAuthorization(TicketPolicy);

        group.MapGet("/", async (ClaimsPrincipal user, ITicketViewService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(user, cancellationToken)));

        group.MapPost("/", async (SaveTicketViewRequest request, ClaimsPrincipal user,
            ITicketViewService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveTicketViewValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                TicketViewOutcome.Success => Results.Created($"/api/ticket-views/{result.View!.Id}", result.View),
                TicketViewOutcome.DuplicateName => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "View name is already used.", detail: result.Error),
                TicketViewOutcome.Forbidden => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden, title: "Ticket views are not available.", detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown ticket view outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, SaveTicketViewRequest request, ClaimsPrincipal user,
            ITicketViewService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveTicketViewValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                TicketViewOutcome.Success => Results.Ok(result.View),
                TicketViewOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Ticket view not found."),
                TicketViewOutcome.DuplicateName => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "View name is already used.", detail: result.Error),
                TicketViewOutcome.Forbidden => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden, title: "Ticket view cannot be changed.", detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown ticket view outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ITicketViewService service,
            CancellationToken cancellationToken) => await service.DeleteAsync(id, user, cancellationToken) switch
        {
            TicketViewOutcome.Success => Results.NoContent(),
            TicketViewOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Ticket view not found."),
            TicketViewOutcome.Forbidden => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Ticket view cannot be deleted.",
                detail: "Only the owner of a shared view can delete it."),
            var outcome => throw new InvalidOperationException($"Unknown ticket view outcome '{outcome}'."),
        });

        return endpoints;
    }

    private sealed class SaveTicketViewValidator : AbstractValidator<SaveTicketViewRequest>
    {
        public SaveTicketViewValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Filter).NotNull();
            RuleFor(request => request.Filter.Search).MaximumLength(200)
                .When(request => request.Filter is not null);
            RuleFor(request => request.Filter.Statuses!).Must(statuses => statuses.Count <= 20)
                .When(request => request.Filter?.Statuses is not null)
                .WithMessage("A view may not filter on more than 20 statuses.");
            RuleFor(request => request.Filter.AssignedTechnicianId).MaximumLength(200)
                .When(request => request.Filter is not null);
        }
    }
}
