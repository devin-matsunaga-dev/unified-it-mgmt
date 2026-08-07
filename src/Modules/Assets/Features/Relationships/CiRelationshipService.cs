using System.Security.Claims;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Platform.Auditing;

namespace Modules.Assets.Features.Relationships;

public sealed class CiRelationshipService(
    AssetsDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService) : ICiRelationshipService
{
    public async Task<CiRelationshipsResponse?> GetForCiAsync(Guid ciId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Cis.AnyAsync(ci => ci.Id == ciId, cancellationToken))
        {
            return null;
        }

        var edges = await Loaded()
            .Where(relationship => relationship.SourceCiId == ciId || relationship.TargetCiId == ciId)
            .OrderBy(relationship => relationship.CreatedAt).ThenBy(relationship => relationship.Id)
            .ToListAsync(cancellationToken);

        return new(
            ciId,
            [.. edges.Where(edge => edge.SourceCiId == ciId).Select(Map)],
            [.. edges.Where(edge => edge.TargetCiId == ciId).Select(Map)]);
    }

    public async Task<CiRelationshipResult> CreateAsync(
        Guid sourceCiId,
        CreateCiRelationshipRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var source = await dbContext.Cis.SingleOrDefaultAsync(ci => ci.Id == sourceCiId, cancellationToken);
        if (source is null)
        {
            return new(CiRelationshipOutcome.CiNotFound);
        }

        // A CI that depends on itself would make every traversal from it meaningless, and no operator
        // means it, so it is refused at the edge rather than absorbed by the cycle guard.
        if (sourceCiId == request.TargetCiId)
        {
            return new(CiRelationshipOutcome.InvalidTarget, Errors: Field(
                nameof(request.TargetCiId), "A CI cannot be related to itself."));
        }

        var target = await dbContext.Cis.SingleOrDefaultAsync(ci => ci.Id == request.TargetCiId, cancellationToken);
        if (target is null)
        {
            return new(CiRelationshipOutcome.InvalidTarget, Errors: Field(
                nameof(request.TargetCiId), $"CI '{request.TargetCiId}' does not exist."));
        }

        // Disposed CIs are frozen records of what left the estate (WP-2.2); wiring new dependencies
        // into or out of one would rewrite history.
        if (source.LifecycleState == CiLifecycleState.Disposed
            || target.LifecycleState == CiLifecycleState.Disposed)
        {
            return new(
                CiRelationshipOutcome.Disposed,
                Error: "A disposed CI cannot gain new relationships.");
        }

        if (await dbContext.CiRelationships.AnyAsync(
                relationship => relationship.SourceCiId == sourceCiId
                    && relationship.TargetCiId == request.TargetCiId
                    && relationship.Type == request.Type,
                cancellationToken))
        {
            return new(
                CiRelationshipOutcome.Duplicate,
                Error: $"'{source.Name}' already {request.Type} '{target.Name}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var relationship = new CiRelationship
        {
            Id = Guid.CreateVersion7(),
            SourceCiId = sourceCiId,
            SourceCi = source,
            TargetCiId = target.Id,
            TargetCi = target,
            Type = request.Type,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CreatedBy = GetActorId(actor),
            CreatedAt = now,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.CiRelationships.Add(relationship);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new CiRelationshipCreated(
                Guid.CreateVersion7(), now, relationship.Id, sourceCiId, target.Id,
                relationship.Type.ToString(), relationship.CreatedBy),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(relationship);
        await auditService.WriteAsync(
            actor, "Created", "CiRelationship", relationship.Id.ToString(), null, response, cancellationToken);
        return new(CiRelationshipOutcome.Success, response);
    }

    public async Task<CiRelationshipOutcome> DeleteAsync(
        Guid relationshipId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var relationship = await Loaded()
            .SingleOrDefaultAsync(item => item.Id == relationshipId, cancellationToken);
        if (relationship is null)
        {
            return CiRelationshipOutcome.RelationshipNotFound;
        }

        var now = DateTimeOffset.UtcNow;
        var before = Map(relationship);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.CiRelationships.Remove(relationship);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new CiRelationshipRemoved(
                Guid.CreateVersion7(), now, relationshipId, before.SourceCiId, before.TargetCiId,
                before.Type.ToString(), GetActorId(actor)),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "CiRelationship", relationshipId.ToString(), before, null, cancellationToken);
        return CiRelationshipOutcome.Success;
    }

    public Task<CiGraphResponse?> GetGraphAsync(
        Guid ciId,
        CiGraphDirection direction,
        int maxDepth,
        CancellationToken cancellationToken) =>
        BuildAsync(ciId, direction, maxDepth, includeRoot: false, cancellationToken);

    public Task<CiGraphResponse?> GetImpactedByAsync(
        Guid ciId,
        int maxDepth,
        CancellationToken cancellationToken) =>
        // "What breaks if this breaks" is the downstream walk; the CI itself is part of the outage,
        // so it sits at depth 0 of the answer rather than outside it.
        BuildAsync(ciId, CiGraphDirection.Descendants, maxDepth, includeRoot: true, cancellationToken);

    private async Task<CiGraphResponse?> BuildAsync(
        Guid ciId,
        CiGraphDirection direction,
        int maxDepth,
        bool includeRoot,
        CancellationToken cancellationToken)
    {
        var root = await dbContext.Cis.SingleOrDefaultAsync(ci => ci.Id == ciId, cancellationToken);
        if (root is null)
        {
            return null;
        }

        var depth = Math.Clamp(maxDepth, 1, CiGraphQuery.MaximumDepth);
        var hops = await CiGraphQuery.WalkAsync(dbContext, ciId, direction, depth, cancellationToken);
        var depthById = hops.ToDictionary(hop => hop.CiId, hop => hop.Depth);

        var reached = depthById.Keys.ToList();
        var cis = await dbContext.Cis.Where(ci => reached.Contains(ci.Id)).ToListAsync(cancellationToken);

        // Every edge between the CIs the walk reached, not only the ones it followed — a shortcut or
        // a cycle-closing edge is part of the picture even though the traversal refused to re-enter it.
        var scope = reached.Append(ciId).ToList();
        var edges = await dbContext.CiRelationships
            .Where(relationship => scope.Contains(relationship.SourceCiId)
                && scope.Contains(relationship.TargetCiId))
            .Select(relationship => new CiGraphEdge(
                relationship.Id, relationship.SourceCiId, relationship.TargetCiId, relationship.Type))
            .ToListAsync(cancellationToken);

        var nodes = cis
            .Select(ci => new CiGraphNode(
                ci.Id, ci.Type, ci.Name, ci.AssetTag, ci.LifecycleState, ci.IsActive, depthById[ci.Id]))
            .ToList();
        if (includeRoot)
        {
            nodes.Add(new(root.Id, root.Type, root.Name, root.AssetTag, root.LifecycleState, root.IsActive, 0));
        }

        return new(
            ciId,
            direction,
            depth,
            MaxDepthReached: depthById.Values.Any(reachedAt => reachedAt == depth),
            ContainsCycle: CiGraphAnalyzer.ContainsCycle(edges),
            [.. nodes.OrderBy(node => node.Depth).ThenBy(node => node.Name).ThenBy(node => node.Id)],
            [.. edges.OrderBy(edge => edge.Id)]);
    }

    /// <summary>Relationships with both ends loaded, so a response can name them without a second query.</summary>
    private IQueryable<CiRelationship> Loaded() =>
        dbContext.CiRelationships
            .Include(relationship => relationship.SourceCi)
            .Include(relationship => relationship.TargetCi);

    private static IReadOnlyDictionary<string, string[]> Field(string name, string message) =>
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [name] = [message] };

    private static CiRelationshipResponse Map(CiRelationship relationship) => new(
        relationship.Id,
        relationship.SourceCiId,
        relationship.SourceCi.Name,
        relationship.SourceCi.Type,
        relationship.TargetCiId,
        relationship.TargetCi.Name,
        relationship.TargetCi.Type,
        relationship.Type,
        relationship.Description,
        relationship.CreatedBy,
        relationship.CreatedAt);

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
