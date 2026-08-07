using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Helpdesk.Features.CannedResponses;

public static class CannedResponseEndpoints
{
    private const string TicketPolicy = "CanManageTickets";

    public static IEndpointRouteBuilder MapCannedResponseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/canned-responses").RequireAuthorization(TicketPolicy);

        group.MapGet("/", async (ClaimsPrincipal user, ICannedResponseService service,
            CancellationToken cancellationToken) => Results.Ok(await service.ListAsync(user, cancellationToken)));

        group.MapPost("/", async (SaveCannedResponseRequest request, ClaimsPrincipal user,
            ICannedResponseService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveCannedResponseValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                CannedResponseOutcome.Success => Results.Created(
                    $"/api/canned-responses/{result.Response!.Id}", result.Response),
                CannedResponseOutcome.DuplicateName => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Canned response name is already used.", detail: result.Error),
                CannedResponseOutcome.Forbidden => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Canned responses are not available.", detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown canned response outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, SaveCannedResponseRequest request, ClaimsPrincipal user,
            ICannedResponseService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveCannedResponseValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CannedResponseOutcome.Success => Results.Ok(result.Response),
                CannedResponseOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Canned response not found."),
                CannedResponseOutcome.DuplicateName => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Canned response name is already used.", detail: result.Error),
                CannedResponseOutcome.Forbidden => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Canned responses are not available.", detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown canned response outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ICannedResponseService service,
            CancellationToken cancellationToken) => await service.DeleteAsync(id, user, cancellationToken) switch
        {
            CannedResponseOutcome.Success => Results.NoContent(),
            CannedResponseOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Canned response not found."),
            CannedResponseOutcome.Forbidden => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden, title: "Canned responses are not available."),
            var outcome => throw new InvalidOperationException($"Unknown canned response outcome '{outcome}'."),
        });

        group.MapPost("/{id:guid}/render", async (Guid id, RenderCannedResponseRequest request, ClaimsPrincipal user,
            ICannedResponseService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RenderAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CannedResponseOutcome.Success => Results.Ok(result.Rendered),
                CannedResponseOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Canned response not found."),
                CannedResponseOutcome.TicketNotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Ticket not found."),
                CannedResponseOutcome.Forbidden => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden, title: "Canned responses are not available."),
                var outcome => throw new InvalidOperationException($"Unknown canned response outcome '{outcome}'."),
            };
        });

        return endpoints;
    }

    private sealed class SaveCannedResponseValidator : AbstractValidator<SaveCannedResponseRequest>
    {
        public SaveCannedResponseValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Body).NotEmpty().MaximumLength(10_000);
        }
    }
}
