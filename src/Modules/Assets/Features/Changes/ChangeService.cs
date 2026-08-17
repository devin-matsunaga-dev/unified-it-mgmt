using System.Security.Claims;

using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Modules.Assets.Features.Relationships;

using Platform.Actors;
using Platform.Auditing;

namespace Modules.Assets.Features.Changes;

/// <summary>
/// Change requests over CIs, and the approval that opens a maintenance window for them.
/// <para>
/// The dependency expansion lives here rather than in the consumer for two reasons. It needs
/// <c>assets.ci_relationships</c>, which only this module may read; and it has to happen once, at the
/// moment somebody agrees to the change, so that an edge added the following week cannot widen a window
/// that was already approved.
/// </para>
/// </summary>
public sealed class ChangeService(
    AssetsDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService) : IChangeService
{
    private const int MaximumPageSize = 200;

    /// <summary>
    /// The most CIs a change may name directly. Well under WP-3.1's 500-device window ceiling, because
    /// the dependents are added on top and the whole set has to fit through it.
    /// </summary>
    internal const int MaximumNamedCis = 200;

    /// <summary>
    /// The most CIs a change may cover once dependents are added, matching the maintenance window's own
    /// limit. Approving a change that would exceed it is refused rather than truncated — a half-scoped
    /// window is one that mutes some of the estate and alerts on the rest, which is worse than neither.
    /// </summary>
    internal const int MaximumTotalCis = 500;

    /// <summary>
    /// How far the dependency walk goes. WP-2.3's default rather than its ceiling: "and the things that
    /// depend on it" is a statement about a service, not about the transitive closure of the estate.
    /// </summary>
    internal const int DependentDepth = 5;

    private const string EntityType = "ChangeRequest";

    public async Task<ChangePageResponse> ListAsync(
        ChangeListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        var query = dbContext.ChangeRequests.AsNoTracking();
        if (request.Statuses is { Count: > 0 } statuses)
        {
            query = query.Where(change => statuses.Contains(change.Status));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(change => EF.Functions.ILike(change.Title, term));
        }

        if (request.CiId is { } ciId)
        {
            query = query.Where(change => change.Cis.Any(scope => scope.CiId == ciId));
        }

        // Overlap, not containment: a change that starts before the month and ends inside it is part of
        // that month, and a calendar that hid it would be showing an estate quieter than it is.
        if (request.From is { } from)
        {
            query = query.Where(change => change.PlannedEndAt >= from);
        }

        if (request.To is { } to)
        {
            query = query.Where(change => change.PlannedStartAt <= to);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(change => change.PlannedStartAt).ThenBy(change => change.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(change => new
            {
                Change = change,
                Named = change.Cis.Count(scope => !scope.IsDependent),
                Dependents = change.Cis.Count(scope => scope.IsDependent),
            })
            .ToListAsync(cancellationToken);

        return new ChangePageResponse(
            [.. items.Select(row => Map(row.Change, row.Named + row.Dependents, row.Dependents, cis: null))],
            total,
            page,
            pageSize);
    }

    public async Task<ChangeResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var change = await dbContext.ChangeRequests.AsNoTracking()
            .Include(item => item.Cis)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return change is null ? null : await MapWithCisAsync(change, cancellationToken);
    }

    public async Task<ChangeResult> CreateAsync(
        CreateChangeRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var ciIds = Normalise(request.CiIds);
        if (await ValidateAsync(request.PlannedStartAt, request.PlannedEndAt, ciIds, cancellationToken)
            is { Count: > 0 } errors)
        {
            return new ChangeResult(ChangeOutcome.Invalid, Errors: errors);
        }

        if (ActorRoles.ActorId(actor) is not { } actorId)
        {
            return new ChangeResult(
                ChangeOutcome.Forbidden, Error: "An authenticated actor identifier is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var change = new ChangeRequest
        {
            Id = Guid.CreateVersion7(),
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Status = ChangeRequestStatus.Draft,
            PlannedStartAt = request.PlannedStartAt,
            PlannedEndAt = request.PlannedEndAt,
            IncludeDependents = request.IncludeDependents,
            RequestedById = actorId,
            RequestedByName = ActorName(actor),
            RequestedAt = now,
            UpdatedAt = now,
            Cis = [.. ciIds.Select(ciId => new ChangeRequestCi { CiId = ciId, IsDependent = false })],
        };

        dbContext.ChangeRequests.Add(change);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await MapWithCisAsync(change, cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", EntityType, change.Id.ToString(), null, response, cancellationToken);
        return new ChangeResult(ChangeOutcome.Success, response);
    }

    public async Task<ChangeResult> UpdateAsync(
        Guid id,
        UpdateChangeRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var change = await dbContext.ChangeRequests
            .Include(item => item.Cis)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (change is null)
        {
            return new ChangeResult(ChangeOutcome.NotFound);
        }

        // Editable only while it is a draft. Once it is with somebody for a decision, what they are
        // deciding about must not move underneath them; once approved, the window already exists.
        if (change.Status != ChangeRequestStatus.Draft)
        {
            return new ChangeResult(
                ChangeOutcome.InvalidTransition,
                Error: $"A change can only be edited while it is a draft; this one is {change.Status}. "
                    + "Return it to draft first.");
        }

        var ciIds = Normalise(request.CiIds);
        if (await ValidateAsync(request.PlannedStartAt, request.PlannedEndAt, ciIds, cancellationToken)
            is { Count: > 0 } errors)
        {
            return new ChangeResult(ChangeOutcome.Invalid, Errors: errors);
        }

        var before = await MapWithCisAsync(change, cancellationToken);

        change.Title = request.Title.Trim();
        change.Description = request.Description.Trim();
        change.PlannedStartAt = request.PlannedStartAt;
        change.PlannedEndAt = request.PlannedEndAt;
        change.IncludeDependents = request.IncludeDependents;
        change.UpdatedAt = DateTimeOffset.UtcNow;

        // A complete statement of the CI list, following WP-2.2's assignment endpoint. Nothing here is a
        // dependent — those only exist after approval, and a draft has not been approved.
        change.Cis.Clear();
        foreach (var ciId in ciIds)
        {
            change.Cis.Add(new ChangeRequestCi { ChangeRequestId = change.Id, CiId = ciId, IsDependent = false });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await MapWithCisAsync(change, cancellationToken);
        await auditService.WriteAsync(
            actor, "Updated", EntityType, change.Id.ToString(), before, response, cancellationToken);
        return new ChangeResult(ChangeOutcome.Success, response);
    }

    public async Task<ChangeResult> TransitionAsync(
        Guid id,
        ChangeTransitionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var change = await dbContext.ChangeRequests
            .Include(item => item.Cis)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (change is null)
        {
            return new ChangeResult(ChangeOutcome.NotFound);
        }

        var actorId = ActorRoles.ActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var verdict = ChangeWorkflow.Check(change, request.TargetStatus, change.Cis.Count, actorId, now);
        if (verdict is not ChangeTransitionVerdict.Allowed)
        {
            var explanation = ChangeWorkflow.Explain(change.Status, request.TargetStatus, verdict);
            return new ChangeResult(
                verdict is ChangeTransitionVerdict.NeedsCis or ChangeTransitionVerdict.WindowHasPassed
                    ? ChangeOutcome.Invalid
                    : verdict is ChangeTransitionVerdict.NeedsASecondPerson
                        ? ChangeOutcome.Forbidden
                        : ChangeOutcome.InvalidTransition,
                Errors: verdict is ChangeTransitionVerdict.WindowHasPassed
                    ? new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [nameof(ChangeRequest.PlannedEndAt)] = [explanation],
                    }
                    : null,
                Error: explanation);
        }

        var before = await MapWithCisAsync(change, cancellationToken);
        var previousStatus = change.Status;
        change.Status = request.TargetStatus;
        change.UpdatedAt = now;

        if (request.TargetStatus is ChangeRequestStatus.Approved or ChangeRequestStatus.Rejected
            or ChangeRequestStatus.Cancelled)
        {
            change.DecidedById = actorId;
            change.DecidedByName = ActorName(actor);
            change.DecidedAt = now;
            change.DecisionNote = Trim(request.Note);
        }

        if (request.TargetStatus != ChangeRequestStatus.Approved)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            var moved = await MapWithCisAsync(change, cancellationToken);
            await auditService.WriteAsync(
                actor, previousStatus == ChangeRequestStatus.Submitted
                    && request.TargetStatus == ChangeRequestStatus.Draft
                    ? "Withdrawn"
                    : request.TargetStatus.ToString(),
                EntityType, change.Id.ToString(), before, moved, cancellationToken);
            return new ChangeResult(ChangeOutcome.Success, moved);
        }

        // Approval: resolve the dependents now, once, and refuse rather than truncate if the answer is
        // bigger than a maintenance window can hold.
        var named = change.Cis.Where(scope => !scope.IsDependent).Select(scope => scope.CiId).ToList();
        var dependents = change.IncludeDependents
            ? await ResolveDependentsAsync(named, cancellationToken)
            : [];
        if (named.Count + dependents.Count > MaximumTotalCis)
        {
            return new ChangeResult(
                ChangeOutcome.Invalid,
                Errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(ChangeRequest.IncludeDependents)] =
                    [
                        $"This change names {named.Count} items and {dependents.Count} depend on them, which is "
                        + $"more than the {MaximumTotalCis} a maintenance window covers. Narrow it, or approve it "
                        + "without dependents.",
                    ],
                });
        }

        foreach (var ciId in dependents)
        {
            change.Cis.Add(new ChangeRequestCi { ChangeRequestId = change.Id, CiId = ciId, IsDependent = true });
        }

        var covered = named.Concat(dependents).ToList();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        // Through the outbox, in the same transaction as the decision: a change that reads as approved
        // and never told Monitoring would be a device somebody believes is muted and is not.
        await publishEndpoint.Publish(
            new ChangeRequestApproved(
                Guid.CreateVersion7(),
                now,
                change.Id,
                change.Number,
                change.Title,
                change.PlannedStartAt,
                change.PlannedEndAt,
                covered),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var approved = await MapWithCisAsync(change, cancellationToken);
        await auditService.WriteAsync(
            actor, "Approved", EntityType, change.Id.ToString(), before, approved, cancellationToken);
        return new ChangeResult(ChangeOutcome.Success, approved);
    }

    /// <summary>
    /// Everything that depends on any of <paramref name="ciIds"/>, minus the named CIs themselves.
    /// <para>
    /// The descendants walk, which WP-2.3 fixed as "what needs this" — the same direction
    /// <c>impacted-by</c> uses, because the question a change asks is exactly a blast radius: rebooting
    /// this switch disturbs everything hanging off it.
    /// </para>
    /// </summary>
    private async Task<List<Guid>> ResolveDependentsAsync(
        IReadOnlyList<Guid> ciIds,
        CancellationToken cancellationToken)
    {
        var named = ciIds.ToHashSet();
        var dependents = new HashSet<Guid>();
        foreach (var ciId in ciIds)
        {
            var hops = await CiGraphQuery.WalkAsync(
                dbContext, ciId, CiGraphDirection.Descendants, DependentDepth, cancellationToken);
            foreach (var hop in hops)
            {
                if (!named.Contains(hop.CiId))
                {
                    dependents.Add(hop.CiId);
                }
            }
        }

        return [.. dependents.Order()];
    }

    private async Task<IReadOnlyDictionary<string, string[]>> ValidateAsync(
        DateTimeOffset plannedStartAt,
        DateTimeOffset plannedEndAt,
        IReadOnlyList<Guid> ciIds,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (plannedEndAt <= plannedStartAt)
        {
            errors[nameof(ChangeRequest.PlannedEndAt)] = ["A change must end after it starts."];
        }

        if (ciIds.Count > MaximumNamedCis)
        {
            errors["CiIds"] = [$"A change names at most {MaximumNamedCis} configuration items."];
            return errors;
        }

        if (ciIds.Count > 0)
        {
            var known = await dbContext.Cis
                .Where(ci => ciIds.Contains(ci.Id))
                .Select(ci => ci.Id)
                .ToListAsync(cancellationToken);
            if (known.Count != ciIds.Count)
            {
                var missing = ciIds.Except(known).Select(id => id.ToString());
                errors["CiIds"] = [$"Unknown configuration items: {string.Join(", ", missing)}."];
            }
        }

        return errors;
    }

    private async Task<ChangeResponse> MapWithCisAsync(ChangeRequest change, CancellationToken cancellationToken)
    {
        var ciIds = change.Cis.Select(scope => scope.CiId).ToList();
        var cis = await dbContext.Cis.AsNoTracking()
            .Where(ci => ciIds.Contains(ci.Id))
            .Select(ci => new { ci.Id, ci.Name, ci.Type, ci.AssetTag, ci.LifecycleState })
            .ToListAsync(cancellationToken);
        var byId = cis.ToDictionary(ci => ci.Id);

        // Named first, then dependents, each by name: the list is read as "what I asked for, and what
        // that turned out to include".
        var rendered = change.Cis
            .Select(scope =>
            {
                var ci = byId.GetValueOrDefault(scope.CiId);
                return new ChangeCiResponse(
                    scope.CiId,
                    ci?.Name,
                    ci?.Type.ToString(),
                    ci?.AssetTag,
                    ci?.LifecycleState.ToString(),
                    scope.IsDependent);
            })
            .OrderBy(scope => scope.IsDependent)
            .ThenBy(scope => scope.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(scope => scope.CiId)
            .ToList();

        return Map(change, rendered.Count, rendered.Count(scope => scope.IsDependent), rendered);
    }

    private static ChangeResponse Map(
        ChangeRequest change,
        int ciCount,
        int dependentCount,
        IReadOnlyList<ChangeCiResponse>? cis) => new(
        change.Id,
        change.Number,
        change.Title,
        change.Description,
        change.Status,
        change.PlannedStartAt,
        change.PlannedEndAt,
        change.IncludeDependents,
        change.RequestedById,
        change.RequestedByName,
        change.RequestedAt,
        change.DecidedById,
        change.DecidedByName,
        change.DecidedAt,
        change.DecisionNote,
        change.UpdatedAt,
        ciCount,
        dependentCount,
        ChangeWorkflow.NextFrom(change.Status),
        cis);

    private static IReadOnlyList<Guid> Normalise(IReadOnlyList<Guid>? ciIds) =>
        ciIds is null ? [] : [.. ciIds.Distinct()];

    private static string ActorName(ClaimsPrincipal actor) =>
        actor.Identity?.Name
        ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "unknown";

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
