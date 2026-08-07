using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Helpdesk.Features.TicketCis;

public static class TicketCiLinkEndpoints
{
    public static IEndpointRouteBuilder MapTicketCiLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tickets/{id:guid}/cis").RequireAuthorization("CanManageTickets");

        group.MapGet("/", async (Guid id, ClaimsPrincipal user, ITicketCiLinkService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(id, user, cancellationToken);
            return result.Outcome switch
            {
                TicketCiLinkOutcome.Success => Results.Ok(result.Links),
                TicketCiLinkOutcome.TicketNotFound => NotFound("Ticket not found."),
                TicketCiLinkOutcome.Forbidden => Forbidden(),
                var outcome => throw new InvalidOperationException($"Unknown ticket CI link outcome '{outcome}'."),
            };
        });

        group.MapPost("/", async (Guid id, LinkTicketCiRequest request, ClaimsPrincipal user,
            ITicketCiLinkService service, CancellationToken cancellationToken) =>
        {
            var validation = await new LinkTicketCiValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.LinkAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                TicketCiLinkOutcome.Success =>
                    Results.Created($"/api/tickets/{id}/cis/{result.Link!.CiId}", result.Link),
                TicketCiLinkOutcome.TicketNotFound => NotFound("Ticket not found."),
                TicketCiLinkOutcome.CiNotFound => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.CiId)] = [result.Error!] }),
                TicketCiLinkOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "CI is already linked to this ticket.",
                    detail: result.Error),
                TicketCiLinkOutcome.Forbidden => Forbidden(),
                var outcome => throw new InvalidOperationException($"Unknown ticket CI link outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{ciId:guid}", async (Guid id, Guid ciId, ClaimsPrincipal user,
            ITicketCiLinkService service, CancellationToken cancellationToken) =>
            await service.UnlinkAsync(id, ciId, user, cancellationToken) switch
            {
                TicketCiLinkOutcome.Success => Results.NoContent(),
                TicketCiLinkOutcome.TicketNotFound => NotFound("Ticket not found."),
                TicketCiLinkOutcome.LinkNotFound => NotFound("The CI is not linked to this ticket."),
                TicketCiLinkOutcome.Forbidden => Forbidden(),
                var outcome => throw new InvalidOperationException($"Unknown ticket CI link outcome '{outcome}'."),
            });

        return endpoints;
    }

    private static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    private static IResult Forbidden() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Assets are not visible to requesters.",
        detail: "Linking a configuration item to a ticket is an agent-only action.");

    private sealed class LinkTicketCiValidator : AbstractValidator<LinkTicketCiRequest>
    {
        public LinkTicketCiValidator() => RuleFor(request => request.CiId).NotEqual(Guid.Empty);
    }
}
