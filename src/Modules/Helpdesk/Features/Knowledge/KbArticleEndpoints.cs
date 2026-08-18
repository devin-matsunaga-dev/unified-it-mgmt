using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Knowledge;

public static class KbArticleEndpoints
{
    private const string TicketPolicy = "CanManageTickets";

    public static IEndpointRouteBuilder MapKbArticleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // CanManageTickets rather than a policy of its own, following WP-5.5's and WP-5.7's call not to add
        // one where the question belongs per record. It deliberately includes EndUser so requesters can
        // reach the portal — which is exactly what the knowledge base needs, because the portal search and
        // the deflection prompt are end-user surfaces. Every *write* below is agent-gated in the service,
        // and every read narrows itself to published for a non-agent inside the query.
        var group = endpoints.MapGroup("/api/kb-articles").RequireAuthorization(TicketPolicy);

        group.MapGet("/", async (
            string? search,
            string? status,
            Guid? categoryId,
            int? page,
            int? pageSize,
            ClaimsPrincipal user,
            IKbArticleService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseStatuses(status, out var statuses))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(status)] = ["Status must be Draft, Published or Archived."],
                });
            }

            var filter = new KbArticleListFilter(search, statuses, categoryId, page ?? 1, pageSize ?? 25);
            return Results.Ok(await service.ListAsync(filter, user, cancellationToken));
        });

        // The one read both the agent's while-typing panel and the portal's deflection prompt make. GET
        // rather than POST despite carrying a paragraph, because it changes nothing and a browser retries
        // and caches it — the description is capped at the edge so a URL cannot grow without bound.
        group.MapGet("/suggestions", async (
            string? subject,
            string? body,
            Guid? categoryId,
            int? limit,
            ClaimsPrincipal user,
            IKbArticleService service,
            CancellationToken cancellationToken) =>
        {
            var request = new KbSuggestionRequest(
                Truncate(subject, 200),
                Truncate(body, 4_000),
                categoryId,
                limit ?? 5);
            return Results.Ok(await service.SuggestAsync(request, user, cancellationToken));
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IKbArticleService service,
            CancellationToken cancellationToken) =>
            await service.GetAsync(id, user, cancellationToken) is { } article
                ? Results.Ok(article)
                // A draft an end user asks for answers the same way an article that does not exist does:
                // "you may not read this one" would confirm that one about their question exists.
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Article not found."));

        group.MapPost("/", async (CreateKbArticleRequest request, ClaimsPrincipal user,
            IKbArticleService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateKbArticleValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                KbOutcome.Success => Results.Created($"/api/kb-articles/{result.Article!.Id}", result.Article),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateKbArticleRequest request, ClaimsPrincipal user,
            IKbArticleService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateKbArticleValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                KbOutcome.Success => Results.Ok(result.Article),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        // POST rather than a field on the update, following the problem and the change request: publishing
        // is an act with an entry condition and a field assignment walks straight past one.
        group.MapPost("/{id:guid}/transitions", async (Guid id, KbTransitionRequest request,
            ClaimsPrincipal user, IKbArticleService service, CancellationToken cancellationToken) =>
        {
            var result = await service.TransitionAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                KbOutcome.Success => Results.Ok(result.Article),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        group.MapPost("/{id:guid}/revisions/{version:int}/restoration", async (Guid id, int version,
            ClaimsPrincipal user, IKbArticleService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RestoreAsync(id, version, user, cancellationToken);
            return result.Outcome switch
            {
                KbOutcome.Success => Results.Ok(result.Article),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IKbArticleService service,
            CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, user, cancellationToken) switch
            {
                KbOutcome.Success => Results.NoContent(),
                KbOutcome.InvalidTransition => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "This article has been used to answer a ticket.",
                    detail: "Archive it instead — those attachments are the record of what somebody was told."),
                var outcome => Failure(outcome, "Article not found."),
            });

        // The other half of the ticket link, read and written from the ticket because that is what it is
        // about — the same placement WP-5.7 gave /api/tickets/{id}/problems.
        var ticketArticles = endpoints.MapGroup("/api/tickets/{ticketId:guid}/kb-articles")
            .RequireAuthorization(TicketPolicy);

        ticketArticles.MapGet("/", async (Guid ticketId, ClaimsPrincipal user, IKbArticleService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListForTicketAsync(ticketId, user, cancellationToken)));

        ticketArticles.MapPost("/", async (Guid ticketId, LinkKbArticleRequest request, ClaimsPrincipal user,
            IKbArticleService service, CancellationToken cancellationToken) =>
        {
            var result = await service.LinkToTicketAsync(ticketId, request.ArticleId, user, cancellationToken);
            return result.Outcome switch
            {
                KbOutcome.Success => Results.Created($"/api/tickets/{ticketId}/kb-articles", result.Link),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        ticketArticles.MapDelete("/{articleId:guid}", async (Guid ticketId, Guid articleId,
            ClaimsPrincipal user, IKbArticleService service, CancellationToken cancellationToken) =>
            await service.UnlinkFromTicketAsync(ticketId, articleId, user, cancellationToken) switch
            {
                KbOutcome.Success => Results.NoContent(),
                var outcome => Failure(outcome, "That article is not attached to this ticket."),
            });

        return endpoints;
    }

    private static IResult Failure(
        KbOutcome outcome,
        string? error,
        IReadOnlyDictionary<string, string[]>? errors = null) => outcome switch
    {
        KbOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Article not found.", detail: error),
        KbOutcome.Invalid => errors is null
            ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.", detail: error)
            : Results.ValidationProblem(errors.ToDictionary(entry => entry.Key, entry => entry.Value)),
        KbOutcome.InvalidTransition => Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "That is not a move this workflow makes.", detail: error),
        KbOutcome.Duplicate => Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Already recorded.", detail: error),
        KbOutcome.Forbidden => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Knowledge articles are not available.", detail: error),
        var unknown => throw new InvalidOperationException($"Unknown knowledge outcome '{unknown}'."),
    };

    /// <summary>
    /// The closed-enum guard in its three-clause form, met for the sixth time. <c>TryParse</c> accepts the
    /// string "3" and 3 <em>is</em> a defined member, so without the name comparison <c>?status=3</c> would
    /// silently filter the list by whichever member sits at that ordinal (WP-5.6).
    /// </summary>
    private static bool TryParseStatuses(string? status, out IReadOnlyList<KbArticleStatus>? statuses)
    {
        statuses = null;
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        var parsed = new List<KbArticleStatus>();
        foreach (var token in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<KbArticleStatus>(token, ignoreCase: true, out var value)
                || !Enum.IsDefined(value)
                || !string.Equals(value.ToString(), token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            parsed.Add(value);
        }

        statuses = parsed;
        return true;
    }

    /// <summary>
    /// Trimmed rather than refused. What arrives here is half a form somebody is still typing into, and a
    /// 400 in the middle of that is an error message about a request they never made.
    /// </summary>
    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maximumLength ? value : value[..maximumLength];

    private sealed class CreateKbArticleValidator : AbstractValidator<CreateKbArticleRequest>
    {
        public CreateKbArticleValidator()
        {
            RuleFor(request => request.Title).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Summary).NotEmpty().MaximumLength(500);
            RuleFor(request => request.Body).NotEmpty().MaximumLength(50_000);
            RuleFor(request => request.Keywords).MaximumLength(500);
        }
    }

    private sealed class UpdateKbArticleValidator : AbstractValidator<UpdateKbArticleRequest>
    {
        public UpdateKbArticleValidator()
        {
            RuleFor(request => request.Title).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Summary).NotEmpty().MaximumLength(500);
            RuleFor(request => request.Body).NotEmpty().MaximumLength(50_000);
            RuleFor(request => request.Keywords).MaximumLength(500);
        }
    }
}
