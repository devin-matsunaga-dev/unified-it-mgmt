using System.Security.Claims;

using FluentValidation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Assets.Data;

namespace Modules.Assets.Features.PhysicalAudits;

public static class PhysicalAuditEndpoints
{
    public static IEndpointRouteBuilder MapPhysicalAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/audit-sessions").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (string? status, int? page, int? pageSize,
            IPhysicalAuditService service, CancellationToken cancellationToken) =>
        {
            if (!TryParseStatus(status, out var parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["status"] = [$"'{status}' is not an audit session status. Use Open, Closed, or all."],
                });
            }

            return Results.Ok(await service.ListAsync(
                new AuditSessionListRequest(parsed, page ?? 1, pageSize ?? 25), cancellationToken));
        });

        group.MapGet("/{id:guid}", async (Guid id, IPhysicalAuditService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } session
                ? Results.Ok(session)
                : NotFound());

        // The discrepancy report. A GET because it is a read of what the session has found so far —
        // running it does not close anything, and an auditor part-way round a floor should be able to
        // see what is still owed.
        group.MapGet("/{id:guid}/report", async (Guid id, IPhysicalAuditService service,
                CancellationToken cancellationToken) =>
            await service.GetReportAsync(id, cancellationToken) is { } report
                ? Results.Ok(report)
                : NotFound());

        group.MapPost("/", async (CreateAuditSessionRequest request, ClaimsPrincipal user,
            IPhysicalAuditService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                AuditSessionOutcome.Success => Results.Created(
                    $"/api/audit-sessions/{result.Session!.Id}", result.Session),
                AuditSessionOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown audit outcome '{outcome}'."),
            };
        });

        group.MapPost("/{id:guid}/scans", async (Guid id, RecordAuditScanRequest request, ClaimsPrincipal user,
            IPhysicalAuditService service, CancellationToken cancellationToken) =>
        {
            var validation = await new ScanValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await service.RecordScanAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                // 201 the first time an asset is confirmed, 200 when it already had been. The second is
                // not an error: two people walking one rack is the normal case, and the handset needs to
                // be able to tell them apart to say "already counted" rather than "counted".
                AuditSessionOutcome.Success => result.Scan!.AlreadyScanned
                    ? Results.Ok(result.Scan)
                    : Results.Created($"/api/audit-sessions/{id}/scans/{result.Scan!.Id}", result.Scan),
                AuditSessionOutcome.NotFound => NotFound(),
                AuditSessionOutcome.UnknownCode => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "No asset matches that code.",
                    detail: result.Error),
                AuditSessionOutcome.Closed => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown audit outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}/scans/{scanId:guid}", async (Guid id, Guid scanId, ClaimsPrincipal user,
            IPhysicalAuditService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RemoveScanAsync(id, scanId, user, cancellationToken);
            return result.Outcome switch
            {
                AuditSessionOutcome.Success => Results.NoContent(),
                AuditSessionOutcome.NotFound => NotFound(),
                AuditSessionOutcome.Closed => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown audit outcome '{outcome}'."),
            };
        });

        // Closing is an action rather than a field somebody sets, so it is a POST to its own
        // sub-resource — the shape WP-1.2 established for a ticket transition.
        group.MapPost("/{id:guid}/closure", async (Guid id, CloseAuditSessionRequest? request, ClaimsPrincipal user,
            IPhysicalAuditService service, CancellationToken cancellationToken) =>
        {
            var result = await service.CloseAsync(
                id, request ?? new CloseAuditSessionRequest(), user, cancellationToken);
            return result.Outcome switch
            {
                AuditSessionOutcome.Success => Results.Ok(result.Session),
                AuditSessionOutcome.NotFound => NotFound(),
                AuditSessionOutcome.Closed => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown audit outcome '{outcome}'."),
            };
        });

        return endpoints;
    }

    private static bool TryParseStatus(string? status, out PhysicalAuditSessionStatus? parsed)
    {
        // Absent means every session, because a list of counts is a history: the one somebody opened
        // last week and the one they closed the week before are equally worth seeing.
        if (string.IsNullOrWhiteSpace(status)
            || string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            parsed = null;
            return true;
        }

        if (Enum.TryParse<PhysicalAuditSessionStatus>(status, ignoreCase: true, out var value))
        {
            parsed = value;
            return true;
        }

        parsed = null;
        return false;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Audit session not found.");

    private static IResult Conflict(string? detail) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "This audit session is closed.",
        detail: detail);

    private sealed class CreateValidator : AbstractValidator<CreateAuditSessionRequest>
    {
        public CreateValidator()
        {
            RuleFor(request => request.Name).NotEmpty()
                .WithMessage("Name the count so it can be told apart from the last one.");
            RuleFor(request => request.Name).MaximumLength(200);
            RuleFor(request => request.Note).MaximumLength(2_000);
        }
    }

    private sealed class ScanValidator : AbstractValidator<RecordAuditScanRequest>
    {
        public ScanValidator()
        {
            RuleFor(request => request.Code).NotEmpty()
                .WithMessage("Scan or type a code to confirm an asset.");
            RuleFor(request => request.Code).MaximumLength(500);
            RuleFor(request => request.Note).MaximumLength(2_000);
        }
    }
}
