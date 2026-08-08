using System.Security.Claims;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Modules.Assets.Features.Cis;
using Platform.Auditing;
using Platform.Directory;

namespace Modules.Assets.Features.Lifecycle;

public sealed class CiLifecycleService(
    AssetsDbContext dbContext,
    IDirectoryService directoryService,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService) : ICiLifecycleService
{
    public async Task<IReadOnlyList<CiLifecycleStateResponse>> GetStatesAsync(CancellationToken cancellationToken)
    {
        var transitions = await dbContext.CiLifecycleTransitions.ToListAsync(cancellationToken);
        return
        [
            .. Enum.GetValues<CiLifecycleState>().Select(state => new CiLifecycleStateResponse(
                state,
                [
                    .. transitions.Where(transition => transition.FromState == state)
                        .Select(transition => transition.ToState)
                        .OrderBy(target => target)
                ]))
        ];
    }

    public async Task<CiLifecycleResult> TransitionAsync(
        Guid ciId,
        TransitionCiRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ci = await LoadAsync(ciId, cancellationToken);
        if (ci is null)
        {
            return new(CiOutcome.NotFound);
        }

        var allowed = await dbContext.CiLifecycleTransitions.AnyAsync(
            transition => transition.FromState == ci.LifecycleState && transition.ToState == request.TargetState,
            cancellationToken);
        if (!allowed)
        {
            return new(
                CiOutcome.IllegalTransition,
                Error: $"A CI cannot move from {ci.LifecycleState} to {request.TargetState}.");
        }

        var actorId = GetActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var before = CiService.Map(ci);
        var fromState = ci.LifecycleState;
        var note = Normalise(request.Note);

        dbContext.CiLifecycleHistory.Add(new CiLifecycleHistory
        {
            Id = Guid.CreateVersion7(),
            CiId = ci.Id,
            FromState = fromState,
            ToState = request.TargetState,
            Note = note,
            ActorId = actorId,
            OccurredAt = now,
        });

        ci.LifecycleState = request.TargetState;
        ci.UpdatedAt = now;

        // A retired or disposed CI is nobody's to hold, so retiring checks it back in and the log
        // records why rather than leaving a stale owner on the record.
        if (request.TargetState is CiLifecycleState.Retired or CiLifecycleState.Disposed
            && ci.OwnerUserId is not null)
        {
            dbContext.CiAssignments.Add(new CiAssignmentEntry
            {
                Id = Guid.CreateVersion7(),
                CiId = ci.Id,
                Action = CiAssignmentAction.CheckIn,
                FromOwnerUserId = ci.OwnerUserId,
                FromOwnerName = ci.OwnerName,
                DepartmentId = ci.DepartmentId,
                DepartmentName = ci.DepartmentName,
                SiteId = ci.SiteId,
                SiteName = ci.SiteName,
                Note = $"Checked in automatically when the CI moved to {request.TargetState}.",
                ActorId = actorId,
                OccurredAt = now,
            });
            ci.OwnerUserId = null;
            ci.OwnerName = null;
            ci.AssignedAt = null;
        }

        // Disposal is the end of the record's working life: it drops out of the active list, and
        // CiService refuses further edits from here on.
        if (request.TargetState == CiLifecycleState.Disposed)
        {
            ci.IsActive = false;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new CiLifecycleChanged(
                Guid.CreateVersion7(), now, ci.Id, ci.Type.ToString(),
                fromState.ToString(), request.TargetState.ToString(), actorId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var after = CiService.Map(ci);
        await auditService.WriteAsync(
            actor, "LifecycleChanged", "Ci", ci.Id.ToString(), before, after, cancellationToken);
        return new(CiOutcome.Success, after);
    }

    public async Task<IReadOnlyList<CiLifecycleHistoryResponse>?> GetHistoryAsync(
        Guid ciId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.Cis.AnyAsync(ci => ci.Id == ciId, cancellationToken))
        {
            return null;
        }

        return await dbContext.CiLifecycleHistory
            .Where(history => history.CiId == ciId)
            .OrderBy(history => history.OccurredAt).ThenBy(history => history.Id)
            .Select(history => new CiLifecycleHistoryResponse(
                history.Id,
                history.CiId,
                history.FromState,
                history.ToState,
                history.Note,
                history.ActorId,
                history.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<CiLifecycleResult> AssignAsync(
        Guid ciId,
        AssignCiRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ci = await LoadAsync(ciId, cancellationToken);
        if (ci is null)
        {
            return new(CiOutcome.NotFound);
        }

        if (ci.LifecycleState == CiLifecycleState.Disposed)
        {
            return new(CiOutcome.Disposed, Error: "A disposed CI cannot be assigned.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        DirectoryUser? owner = null;
        DirectoryDepartment? department = null;
        DirectorySite? site = null;

        if (request.OwnerUserId is { } ownerId)
        {
            owner = await directoryService.FindUserAsync(ownerId, cancellationToken);
            if (owner is null)
            {
                errors[nameof(request.OwnerUserId)] = [$"User '{ownerId}' does not exist."];
            }
        }

        if (request.DepartmentId is { } departmentId)
        {
            department = await directoryService.FindDepartmentAsync(departmentId, cancellationToken);
            if (department is null)
            {
                errors[nameof(request.DepartmentId)] = [$"Department '{departmentId}' does not exist."];
            }
        }

        if (request.SiteId is { } siteId)
        {
            site = await directoryService.FindSiteAsync(siteId, cancellationToken);
            if (site is null)
            {
                errors[nameof(request.SiteId)] = [$"Site '{siteId}' does not exist."];
            }
        }

        if (errors.Count > 0)
        {
            return new(CiOutcome.UnknownAssignee, Errors: errors);
        }

        var unchanged = ci.OwnerUserId == owner?.Id
            && ci.DepartmentId == department?.Id
            && ci.SiteId == site?.Id;
        if (unchanged)
        {
            return new(CiOutcome.Success, CiService.Map(ci));
        }

        var actorId = GetActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var before = CiService.Map(ci);
        var action = (ci.OwnerUserId, owner) switch
        {
            (null, not null) => CiAssignmentAction.CheckOut,
            (not null, null) => CiAssignmentAction.CheckIn,
            (not null, not null) when ci.OwnerUserId != owner.Id => CiAssignmentAction.Transfer,
            // The holder did not change, so this write moved the CI between a department or site.
            _ => CiAssignmentAction.Relocate,
        };

        dbContext.CiAssignments.Add(new CiAssignmentEntry
        {
            Id = Guid.CreateVersion7(),
            CiId = ci.Id,
            Action = action,
            FromOwnerUserId = ci.OwnerUserId,
            FromOwnerName = ci.OwnerName,
            ToOwnerUserId = owner?.Id,
            ToOwnerName = owner?.DisplayName,
            DepartmentId = department?.Id,
            DepartmentName = department?.Name,
            SiteId = site?.Id,
            SiteName = site?.Name,
            Note = Normalise(request.Note),
            ActorId = actorId,
            OccurredAt = now,
        });

        ci.OwnerUserId = owner?.Id;
        ci.OwnerName = owner?.DisplayName;
        ci.DepartmentId = department?.Id;
        ci.DepartmentName = department?.Name;
        ci.SiteId = site?.Id;
        ci.SiteName = site?.Name;
        ci.AssignedAt = owner is null && department is null && site is null ? null : now;
        ci.UpdatedAt = now;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new CiAssignmentChanged(
                Guid.CreateVersion7(), now, ci.Id, ci.Type.ToString(), action.ToString(),
                owner?.Id, department?.Id, site?.Id, actorId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var after = CiService.Map(ci);
        await auditService.WriteAsync(
            actor, "AssignmentChanged", "Ci", ci.Id.ToString(), before, after, cancellationToken);
        return new(CiOutcome.Success, after);
    }

    public async Task<IReadOnlyList<CiAssignmentResponse>?> GetAssignmentsAsync(
        Guid ciId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.Cis.AnyAsync(ci => ci.Id == ciId, cancellationToken))
        {
            return null;
        }

        return await dbContext.CiAssignments
            .Where(entry => entry.CiId == ciId)
            .OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id)
            .Select(entry => new CiAssignmentResponse(
                entry.Id,
                entry.CiId,
                entry.Action,
                entry.FromOwnerUserId,
                entry.FromOwnerName,
                entry.ToOwnerUserId,
                entry.ToOwnerName,
                entry.DepartmentId,
                entry.DepartmentName,
                entry.SiteId,
                entry.SiteName,
                entry.Note,
                entry.ActorId,
                entry.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    private Task<ConfigurationItem?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Cis.Include(ci => ci.CustomFieldValues).ThenInclude(value => value.Field)
            .Include(ci => ci.Contract).ThenInclude(contract => contract!.Vendor)
            .SingleOrDefaultAsync(ci => ci.Id == id, cancellationToken);

    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
