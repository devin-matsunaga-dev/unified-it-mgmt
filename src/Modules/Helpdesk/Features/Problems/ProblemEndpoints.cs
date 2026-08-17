using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Helpdesk.Data;
using Platform.Actors;

namespace Modules.Helpdesk.Features.Problems;

public static class ProblemEndpoints
{
    private const string TicketPolicy = "CanManageTickets";

    public static IEndpointRouteBuilder MapProblemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // CanManageTickets rather than a policy of its own, following WP-5.5's call not to add one where
        // the question is per record. It includes EndUser so requesters can reach the portal, so every
        // handler below is agent-gated in the service — a problem names causes, workarounds and other
        // people's incidents.
        var group = endpoints.MapGroup("/api/problems").RequireAuthorization(TicketPolicy);

        group.MapGet("/", async (
            string? search,
            string? status,
            bool? knownErrorsOnly,
            Guid? ciId,
            Guid? categoryId,
            int? page,
            int? pageSize,
            ClaimsPrincipal user,
            IProblemService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseStatuses(status, out var statuses))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(status)] = ["Status must be Investigating, KnownError, Resolved or Closed."],
                });
            }

            var filter = new ProblemListFilter(
                search, statuses, knownErrorsOnly ?? false, ciId, categoryId, page ?? 1, pageSize ?? 25);
            return Results.Ok(await service.ListAsync(filter, user, cancellationToken));
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IProblemService service,
            CancellationToken cancellationToken) =>
            await service.GetAsync(id, user, cancellationToken) is { } problem
                ? Results.Ok(problem)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Problem not found."));

        group.MapPost("/", async (CreateProblemRequest request, ClaimsPrincipal user,
            IProblemService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateProblemValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                ProblemOutcome.Success => Results.Created($"/api/problems/{result.Problem!.Id}", result.Problem),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateProblemRequest request, ClaimsPrincipal user,
            IProblemService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateProblemValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                ProblemOutcome.Success => Results.Ok(result.Problem),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        // POST rather than PUT because a state change is an act with entry conditions and not an
        // assignment, which is the same reasoning WP-1.2 applied to a ticket's transitions.
        group.MapPost("/{id:guid}/transitions", async (Guid id, ProblemTransitionRequest request,
            ClaimsPrincipal user, IProblemService service, CancellationToken cancellationToken) =>
        {
            var result = await service.TransitionAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                ProblemOutcome.Success => Results.Ok(new
                {
                    problem = result.Problem,
                    // Present only when closing. The WP's "closing a problem prompts a KB article",
                    // carried on the response so the prompt cannot be lost to a second request.
                    knowledgeDraft = result.KnowledgeDraft,
                }),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        group.MapGet("/{id:guid}/knowledge-draft", async (Guid id, ClaimsPrincipal user,
            IProblemService service, CancellationToken cancellationToken) =>
            await service.GetKnowledgeDraftAsync(id, user, cancellationToken) is { } draft
                ? Results.Ok(draft)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Problem not found."));

        group.MapPost("/{id:guid}/incidents", async (Guid id, LinkIncidentRequest request,
            ClaimsPrincipal user, IProblemService service, CancellationToken cancellationToken) =>
        {
            var result = await service.LinkIncidentAsync(id, request.TicketId, user, cancellationToken);
            return result.Outcome switch
            {
                ProblemOutcome.Success => Results.Created($"/api/problems/{id}", result.Problem),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        group.MapDelete("/{id:guid}/incidents/{ticketId:guid}", async (Guid id, Guid ticketId,
            ClaimsPrincipal user, IProblemService service, CancellationToken cancellationToken) =>
            await service.UnlinkIncidentAsync(id, ticketId, user, cancellationToken) switch
            {
                ProblemOutcome.Success => Results.NoContent(),
                var outcome => Failure(outcome, "That incident is not linked to this problem."),
            });

        // The other half of the link, read from the incident. Lives under /api/tickets because that is
        // what it is about, and is the reason a technician holding a fresh incident learns there is a
        // known error with a workaround for it.
        endpoints.MapGet("/api/tickets/{ticketId:guid}/problems", async (Guid ticketId, ClaimsPrincipal user,
            IProblemService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListForTicketAsync(ticketId, user, cancellationToken)))
            .RequireAuthorization(TicketPolicy);

        return endpoints;
    }

    public static IEndpointRouteBuilder MapProblemSuggestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/problem-suggestions").RequireAuthorization(TicketPolicy);

        group.MapGet("/", async (string? status, ClaimsPrincipal user,
            IProblemSuggestionService service, CancellationToken cancellationToken) =>
        {
            if (!TryParseSuggestionStatus(status, out var parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(status)] = ["Status must be Open, Accepted or Dismissed."],
                });
            }

            return Results.Ok(await service.ListAsync(parsed, user, cancellationToken));
        });

        // Runs the pass now. It exists because the alternative is that nobody can see this feature work
        // without waiting for the small hours, and because the pass is idempotent — pressing it twice
        // finds its own suggestions open and raises nothing.
        group.MapPost("/detect", async (ClaimsPrincipal user, IProblemSuggestionService service,
            CancellationToken cancellationToken) => !ActorRoles.IsAgent(user)
                ? Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Recurrence detection is an agent surface.")
                : Results.Ok(await service.DetectAsync(user, cancellationToken)));

        group.MapPost("/{id:guid}/acceptance", async (Guid id, AcceptProblemSuggestionRequest? request,
            ClaimsPrincipal user, IProblemSuggestionService service, CancellationToken cancellationToken) =>
        {
            var body = request ?? new AcceptProblemSuggestionRequest();
            var validation = await new AcceptSuggestionValidator().ValidateAsync(body, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.AcceptAsync(id, body, user, cancellationToken);
            return result.Outcome switch
            {
                ProblemOutcome.Success => Results.Ok(result.Suggestion),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        group.MapPost("/{id:guid}/dismissal", async (Guid id, DismissProblemSuggestionRequest? request,
            ClaimsPrincipal user, IProblemSuggestionService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DismissAsync(id, request ?? new DismissProblemSuggestionRequest(),
                user, cancellationToken);
            return result.Outcome switch
            {
                ProblemOutcome.Success => Results.Ok(result.Suggestion),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        return endpoints;
    }

    private static IResult Failure(
        ProblemOutcome outcome,
        string? error,
        IReadOnlyDictionary<string, string[]>? errors = null) => outcome switch
    {
        ProblemOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Problem not found.", detail: error),
        ProblemOutcome.Invalid => errors is null
            ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.", detail: error)
            : Results.ValidationProblem(errors.ToDictionary(entry => entry.Key, entry => entry.Value)),
        ProblemOutcome.InvalidTransition => Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "That is not a move this workflow makes.", detail: error),
        ProblemOutcome.Duplicate => Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Already recorded.", detail: error),
        ProblemOutcome.Forbidden => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Problems are not available.", detail: error),
        var unknown => throw new InvalidOperationException($"Unknown problem outcome '{unknown}'."),
    };

    /// <summary>
    /// The closed-enum guard in its three-clause form, met for the fifth time. <c>TryParse</c> accepts the
    /// string "3" and 3 <em>is</em> a defined member, so without the name comparison <c>?status=3</c> would
    /// silently filter the board by whichever member sits at that ordinal (WP-5.6).
    /// </summary>
    private static bool TryParseStatuses(string? status, out IReadOnlyList<ProblemStatus>? statuses)
    {
        statuses = null;
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        var parsed = new List<ProblemStatus>();
        foreach (var token in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<ProblemStatus>(token, ignoreCase: true, out var value)
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

    private static bool TryParseSuggestionStatus(string? status, out ProblemSuggestionStatus? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        var token = status.Trim();
        if (!Enum.TryParse<ProblemSuggestionStatus>(token, ignoreCase: true, out var value)
            || !Enum.IsDefined(value)
            || !string.Equals(value.ToString(), token, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        parsed = value;
        return true;
    }

    private sealed class CreateProblemValidator : AbstractValidator<CreateProblemRequest>
    {
        public CreateProblemValidator()
        {
            RuleFor(request => request.Title).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).NotEmpty().MaximumLength(10_000);
            RuleFor(request => request.RootCause).MaximumLength(10_000);
            RuleFor(request => request.Workaround).MaximumLength(10_000);
            RuleFor(request => request.AssignedTechnicianId).MaximumLength(200);
            RuleFor(request => request.Priority).IsInEnum();
            RuleFor(request => request.IncidentIds!).Must(ids => ids.Count <= 500)
                .When(request => request.IncidentIds is not null)
                .WithMessage("A problem cannot be opened with more than 500 incidents attached at once.");
        }
    }

    private sealed class UpdateProblemValidator : AbstractValidator<UpdateProblemRequest>
    {
        public UpdateProblemValidator()
        {
            RuleFor(request => request.Title).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).NotEmpty().MaximumLength(10_000);
            RuleFor(request => request.RootCause).MaximumLength(10_000);
            RuleFor(request => request.Workaround).MaximumLength(10_000);
            RuleFor(request => request.AssignedTechnicianId).MaximumLength(200);
            RuleFor(request => request.Priority).IsInEnum();
        }
    }

    private sealed class AcceptSuggestionValidator : AbstractValidator<AcceptProblemSuggestionRequest>
    {
        public AcceptSuggestionValidator()
        {
            RuleFor(request => request.Title).MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(10_000);
            RuleFor(request => request.Priority!.Value).IsInEnum().When(request => request.Priority is not null);
        }
    }
}

public sealed record LinkIncidentRequest(Guid TicketId);
