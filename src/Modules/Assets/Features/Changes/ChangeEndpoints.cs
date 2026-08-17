using System.Security.Claims;

using FluentValidation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Changes;

public static class ChangeEndpoints
{
    /// <summary>
    /// <c>CanManageAssets</c> rather than a policy of its own, following WP-2.1: this is a statement about
    /// configuration items and the CMDB is an agent-only surface. Unlike <c>CanManageTickets</c> it does
    /// not include EndUser, so nothing below needs a second gate in the service — the one distinction this
    /// feature draws between operators is that nobody approves their own change, and that is a question
    /// about a record rather than about a role, so <see cref="ChangeWorkflow"/> answers it.
    /// </summary>
    private const string AssetPolicy = "CanManageAssets";

    public static IEndpointRouteBuilder MapChangeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/changes").RequireAuthorization(AssetPolicy);

        group.MapGet("/", async (
            string? search,
            string? status,
            Guid? ciId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? page,
            int? pageSize,
            IChangeService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseStatuses(status, out var statuses))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(status)] = ["Status must be Draft, Submitted, Approved, Rejected or Cancelled."],
                });
            }

            var request = new ChangeListRequest(search, statuses, ciId, from, to, page ?? 1, pageSize ?? 25);
            return Results.Ok(await service.ListAsync(request, cancellationToken));
        });

        group.MapGet("/{id:guid}", async (Guid id, IChangeService service, CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } change ? Results.Ok(change) : NotFound());

        group.MapPost("/", async (CreateChangeRequest request, ClaimsPrincipal user,
            IChangeService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateChangeValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                ChangeOutcome.Success => Results.Created($"/api/changes/{result.Change!.Id}", result.Change),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateChangeRequest request, ClaimsPrincipal user,
            IChangeService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateChangeValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                ChangeOutcome.Success => Results.Ok(result.Change),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        // POST rather than PUT, following WP-1.2 and WP-5.7: a state change is an act with entry
        // conditions, and approving one has a consequence in another module.
        group.MapPost("/{id:guid}/transitions", async (Guid id, ChangeTransitionRequest request,
            ClaimsPrincipal user, IChangeService service, CancellationToken cancellationToken) =>
        {
            var validation = await new TransitionValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.TransitionAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                ChangeOutcome.Success => Results.Ok(result.Change),
                _ => Failure(result.Outcome, result.Error, result.Errors),
            };
        });

        return endpoints;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Change request not found.");

    private static IResult Failure(
        ChangeOutcome outcome,
        string? error,
        IReadOnlyDictionary<string, string[]>? errors = null) => outcome switch
    {
        ChangeOutcome.NotFound => NotFound(),
        ChangeOutcome.Invalid => errors is null
            ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.", detail: error)
            : Results.ValidationProblem(errors.ToDictionary(entry => entry.Key, entry => entry.Value)),
        ChangeOutcome.InvalidTransition => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That is not a move this workflow makes.",
            detail: error),
        ChangeOutcome.Forbidden => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Not permitted.", detail: error),
        var unknown => throw new InvalidOperationException($"Unknown change outcome '{unknown}'."),
    };

    /// <summary>
    /// The closed-enum guard in its three-clause form (WP-5.6). <c>TryParse</c> accepts the string "3" and
    /// 3 <em>is</em> a defined member, so without the name comparison <c>?status=3</c> would silently
    /// filter the calendar by whichever member sits at that ordinal.
    /// </summary>
    private static bool TryParseStatuses(string? status, out IReadOnlyList<ChangeRequestStatus>? statuses)
    {
        statuses = null;
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        var parsed = new List<ChangeRequestStatus>();
        foreach (var token in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<ChangeRequestStatus>(token, ignoreCase: true, out var value)
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

    private sealed class CreateChangeValidator : AbstractValidator<CreateChangeRequest>
    {
        public CreateChangeValidator()
        {
            RuleFor(request => request.Title).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).NotEmpty().MaximumLength(10_000);
            RuleFor(request => request.CiIds).NotNull();
        }
    }

    private sealed class UpdateChangeValidator : AbstractValidator<UpdateChangeRequest>
    {
        public UpdateChangeValidator()
        {
            RuleFor(request => request.Title).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).NotEmpty().MaximumLength(10_000);
            RuleFor(request => request.CiIds).NotNull();
        }
    }

    private sealed class TransitionValidator : AbstractValidator<ChangeTransitionRequest>
    {
        public TransitionValidator()
        {
            RuleFor(request => request.TargetStatus).IsInEnum();
            RuleFor(request => request.Note).MaximumLength(2_000);
        }
    }
}
