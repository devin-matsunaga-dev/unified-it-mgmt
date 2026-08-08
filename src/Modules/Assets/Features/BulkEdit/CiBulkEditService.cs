using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Modules.Assets.Features.Cis;
using Modules.Assets.Features.Lifecycle;

namespace Modules.Assets.Features.BulkEdit;

/// <summary>
/// Applies one ownership statement and/or one lifecycle move to a selection of CIs. Both halves go
/// through <see cref="ICiLifecycleService"/>, so the WP-2.2 transition graph, the check-in/out log, the
/// audit entry and the disposed-CI freeze all behave exactly as they do for a single CI.
///
/// One refused CI never stops the batch: each row reports its own outcome and the rest still apply.
/// </summary>
public sealed class CiBulkEditService(
    AssetsDbContext dbContext,
    ICiLifecycleService lifecycleService) : ICiBulkEditService
{
    internal const int MaximumSelection = 200;

    public async Task<BulkEditReport> ApplyAsync(
        BulkEditCisRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ids = request.CiIds.Distinct().ToList();
        var names = await dbContext.Cis.Where(ci => ids.Contains(ci.Id))
            .ToDictionaryAsync(ci => ci.Id, ci => ci.Name, cancellationToken);

        var rows = new List<BulkEditRowResult>(ids.Count);
        foreach (var id in ids)
        {
            rows.Add(await ApplyOneAsync(id, names.GetValueOrDefault(id), request, actor, cancellationToken));
        }

        return new(
            rows.Count,
            rows.Count(row => row.Succeeded),
            rows.Count(row => !row.Succeeded),
            rows);
    }

    private async Task<BulkEditRowResult> ApplyOneAsync(
        Guid id,
        string? name,
        BulkEditCisRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (name is null)
        {
            return new(id, null, false, "The CI no longer exists.");
        }

        // Ownership is applied first so that retiring a selection still checks its holders in: the
        // lifecycle move is what clears the owner, and it has to run against the final assignment.
        if (request.Ownership is { } ownership)
        {
            var assigned = await lifecycleService.AssignAsync(
                id,
                new AssignCiRequest(ownership.OwnerUserId, ownership.DepartmentId, ownership.SiteId, request.Note),
                actor,
                cancellationToken);
            if (assigned.Outcome != CiOutcome.Success)
            {
                return new(id, name, false, Describe(assigned));
            }
        }

        if (request.LifecycleState is not { } target)
        {
            return new(id, name, true, null);
        }

        // A CI already in the target state is not a failure — a selection of ten where three are
        // already Deployed should report ten successes, not three errors.
        var current = await dbContext.Cis.Where(ci => ci.Id == id)
            .Select(ci => ci.LifecycleState).SingleOrDefaultAsync(cancellationToken);
        if (current == target)
        {
            return new(id, name, true, null);
        }

        var transitioned = await lifecycleService.TransitionAsync(
            id, new TransitionCiRequest(target, request.Note), actor, cancellationToken);
        return transitioned.Outcome == CiOutcome.Success
            ? new(id, name, true, null)
            : new(id, name, false, Describe(transitioned));
    }

    private static string Describe(CiLifecycleResult result) =>
        result.Error
        ?? result.Errors?.SelectMany(entry => entry.Value).FirstOrDefault()
        ?? result.Outcome switch
        {
            CiOutcome.NotFound => "The CI no longer exists.",
            var outcome => $"The change was refused ({outcome}).",
        };
}
