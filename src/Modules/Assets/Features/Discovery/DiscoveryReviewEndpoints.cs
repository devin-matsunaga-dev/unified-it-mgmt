using System.Security.Claims;

using FluentValidation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Discovery;

public static class DiscoveryReviewEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // `CanManageAssets` and not `CanDiscover`. The scanner's own role reaches its scan profiles and
        // nothing else (WP-4.1): a scanner that could approve its own findings into the CMDB would make
        // the review queue decorative, which is the whole point of the queue. The reverse holds too —
        // an operator has no business fetching a scanner's work list.
        var group = endpoints.MapGroup("/api/discovered-devices")
            .RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (string? status, string? search, Guid? scanProfileId, int? page, int? pageSize,
            IDiscoveryReviewService service, CancellationToken cancellationToken) =>
        {
            if (!TryParseStatus(status, out var parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["status"] = [$"'{status}' is not a discovered-device status."],
                });
            }

            return Results.Ok(await service.ListAsync(
                new DiscoveredDeviceListRequest(parsed, search, scanProfileId, page ?? 1, pageSize ?? 25),
                cancellationToken));
        });

        group.MapGet("/{id:guid}", async (Guid id, IDiscoveryReviewService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } device
                ? Results.Ok(device)
                : NotFound());

        group.MapPost("/{id:guid}/approvals", async (Guid id, ApproveDiscoveredDeviceRequest request,
            ClaimsPrincipal user, IDiscoveryReviewService service, CancellationToken cancellationToken) =>
        {
            var validation = await new ApproveValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await service.ApproveAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                DiscoveryReviewOutcome.Success => Results.Ok(result.Device),
                DiscoveryReviewOutcome.NotFound => NotFound(),
                DiscoveryReviewOutcome.AlreadyReviewed => Conflict(result.Error),
                DiscoveryReviewOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                // The CI service's refusal, forwarded whole. Its errors are keyed by field so the form
                // can attach them; the ones with no field (a duplicate asset tag) become the detail.
                DiscoveryReviewOutcome.CiRejected => result.Errors is { Count: > 0 }
                    ? Results.ValidationProblem(result.Errors)
                    : Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown review outcome '{outcome}'."),
            };
        });

        group.MapPost("/{id:guid}/rejections", async (Guid id, RejectDiscoveredDeviceRequest? request,
            ClaimsPrincipal user, IDiscoveryReviewService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RejectAsync(id, request ?? new RejectDiscoveredDeviceRequest(), user, cancellationToken);
            return result.Outcome switch
            {
                DiscoveryReviewOutcome.Success => Results.Ok(result.Device),
                DiscoveryReviewOutcome.NotFound => NotFound(),
                DiscoveryReviewOutcome.AlreadyReviewed => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown review outcome '{outcome}'."),
            };
        });

        // Filed under the CI rather than under the queue, because that is the question being asked:
        // "what did discovery last see about this asset". Same policy — it is a CMDB read.
        endpoints.MapGet("/api/cis/{ciId:guid}/discovery-facts", async (Guid ciId,
                IDiscoveryReviewService service, CancellationToken cancellationToken) =>
            await service.GetFactsAsync(ciId, cancellationToken) is { } facts
                ? Results.Ok(facts)
                : Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "No scan has reported this CI."))
            .RequireAuthorization("CanManageAssets");

        return endpoints;
    }

    private static bool TryParseStatus(string? status, out DiscoveredDeviceStatus? parsed)
    {
        // Absent means "the queue" — the pending cards, which is what somebody opening this screen
        // wants. `all` is the explicit way to ask for the history including the ignore list.
        if (string.IsNullOrWhiteSpace(status))
        {
            parsed = DiscoveredDeviceStatus.Pending;
            return true;
        }

        if (string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            parsed = null;
            return true;
        }

        if (Enum.TryParse<DiscoveredDeviceStatus>(status, ignoreCase: true, out var value))
        {
            parsed = value;
            return true;
        }

        parsed = null;
        return false;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Discovered device not found.");

    private static IResult Conflict(string? detail) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "This discovery has already been reviewed.",
        detail: detail);

    private sealed class ApproveValidator : AbstractValidator<ApproveDiscoveredDeviceRequest>
    {
        public ApproveValidator()
        {
            RuleFor(request => request.Name).MaximumLength(200);
            RuleFor(request => request.AssetTag).MaximumLength(100);
            RuleFor(request => request.SerialNumber).MaximumLength(100);
            RuleFor(request => request.Description).MaximumLength(2_000);
            RuleFor(request => request.PollerGroup).MaximumLength(100);
            RuleFor(request => request.Note).MaximumLength(2_000);

            // Attaching to an existing CI and creating a new one are two different decisions, and a
            // request that states both is one where the caller has not made either.
            RuleFor(request => request.Type)
                .Null()
                .When(request => request.CiId is not null)
                .WithMessage("Approving onto an existing CI cannot also create one; omit the type and attributes.");
            RuleFor(request => request.Attributes)
                .Null()
                .When(request => request.CiId is not null)
                .WithMessage("Approving onto an existing CI cannot also create one; omit the type and attributes.");
        }
    }
}
