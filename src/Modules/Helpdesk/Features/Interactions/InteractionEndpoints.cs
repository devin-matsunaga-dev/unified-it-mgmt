using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Helpdesk.Features.Interactions;

public static class InteractionEndpoints
{
    private const string TicketPolicy = "CanManageTickets";

    public static IEndpointRouteBuilder MapInteractionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tickets/{ticketId:guid}").RequireAuthorization(TicketPolicy);

        group.MapPost("/comments", async (
            Guid ticketId, CreateCommentRequest request, ClaimsPrincipal user,
            IInteractionService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateCommentRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await service.AddCommentAsync(ticketId, request, user, cancellationToken);
            return ToWriteResult(result, $"/api/tickets/{ticketId}/comments/{result.Value?.Id}");
        });

        group.MapGet("/comments", async (
            Guid ticketId, ClaimsPrincipal user, IInteractionService service, CancellationToken cancellationToken) =>
        {
            var comments = await service.GetCommentsAsync(ticketId, user, cancellationToken);
            return comments is null ? NotFound() : Results.Ok(comments);
        });

        group.MapPost("/worklogs", async (
            Guid ticketId, CreateWorklogRequest request, ClaimsPrincipal user,
            IInteractionService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateWorklogRequestValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await service.AddWorklogAsync(ticketId, request, user, cancellationToken);
            return ToWriteResult(result, $"/api/tickets/{ticketId}/worklogs/{result.Value?.Id}");
        });

        group.MapGet("/worklogs", async (
            Guid ticketId, ClaimsPrincipal user, IInteractionService service, CancellationToken cancellationToken) =>
            ToReadResult(await service.GetWorklogsAsync(ticketId, user, cancellationToken)));

        group.MapPost("/attachments", async (
            Guid ticketId, IFormFile file, ClaimsPrincipal user,
            IInteractionService service, CancellationToken cancellationToken) =>
        {
            var result = await service.AddAttachmentAsync(ticketId, file, user, cancellationToken);
            return ToWriteResult(result, $"/api/tickets/{ticketId}/attachments/{result.Value?.Id}");
        }).DisableAntiforgery();

        group.MapGet("/attachments", async (
            Guid ticketId, ClaimsPrincipal user, IInteractionService service, CancellationToken cancellationToken) =>
        {
            var attachments = await service.GetAttachmentsAsync(ticketId, user, cancellationToken);
            return attachments is null ? NotFound() : Results.Ok(attachments);
        });

        group.MapGet("/attachments/{attachmentId:guid}", async (
            Guid ticketId, Guid attachmentId, ClaimsPrincipal user,
            IInteractionService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DownloadAttachmentAsync(ticketId, attachmentId, user, cancellationToken);
            return result.Outcome switch
            {
                InteractionOutcome.Success => Results.File(
                    result.Value!.Content, result.Value.ContentType, result.Value.FileName),
                InteractionOutcome.NotFound => NotFound(),
                _ => throw new InvalidOperationException($"Unexpected download outcome '{result.Outcome}'."),
            };
        });

        return endpoints;
    }

    private static IResult ToWriteResult<T>(InteractionResult<T> result, string location) => result.Outcome switch
    {
        InteractionOutcome.Success => Results.Created(location, result.Value),
        InteractionOutcome.NotFound => NotFound(),
        InteractionOutcome.Forbidden => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.", detail: result.Error),
        InteractionOutcome.InvalidFile => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["file"] = [result.Error!] }),
        InteractionOutcome.ScanRejected => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["file"] = [result.Error!] }),
        _ => throw new InvalidOperationException($"Unexpected interaction outcome '{result.Outcome}'."),
    };

    private static IResult ToReadResult<T>(InteractionResult<T> result) => result.Outcome switch
    {
        InteractionOutcome.Success => Results.Ok(result.Value),
        InteractionOutcome.NotFound => NotFound(),
        InteractionOutcome.Forbidden => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.", detail: result.Error),
        _ => throw new InvalidOperationException($"Unexpected interaction outcome '{result.Outcome}'."),
    };

    private static IResult NotFound() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound, title: "Ticket or interaction not found.");
}
