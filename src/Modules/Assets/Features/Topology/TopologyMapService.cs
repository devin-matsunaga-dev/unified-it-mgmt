using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Platform.Auditing;

namespace Modules.Assets.Features.Topology;

public sealed class TopologyMapService(AssetsDbContext dbContext, IAuditService auditService) : ITopologyMapService
{
    public async Task<IReadOnlyList<TopologyMapSummaryResponse>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.TopologyMaps.AsNoTracking()
            .OrderBy(map => map.Name)
            .Select(map => new TopologyMapSummaryResponse(
                map.Id,
                map.Name,
                map.Description,
                map.Nodes.Count,
                map.CreatedBy,
                map.CreatedAt,
                map.UpdatedBy,
                map.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<TopologyMapResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await Loaded().AsNoTracking().SingleOrDefaultAsync(map => map.Id == id, cancellationToken) is { } map
            ? Map(map)
            : null;

    public async Task<TopologyMapResult> CreateAsync(
        SaveTopologyMapRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name.Trim();
        if (await dbContext.TopologyMaps.AnyAsync(map => map.Name.ToLower() == name.ToLower(), cancellationToken))
        {
            return Duplicate(name);
        }

        if (await UnknownCiAsync(request, cancellationToken) is { } unknown)
        {
            return unknown;
        }

        var now = DateTimeOffset.UtcNow;
        var created = new TopologyMap
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = Trimmed(request.Description),
            CreatedBy = GetActorId(actor),
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var node in Distinct(request.Nodes))
        {
            created.Nodes.Add(new TopologyMapNode
            {
                Id = Guid.CreateVersion7(),
                TopologyMapId = created.Id,
                CiId = node.CiId,
                X = node.X,
                Y = node.Y,
            });
        }

        dbContext.TopologyMaps.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(created);
        await auditService.WriteAsync(
            actor, "Created", "TopologyMap", created.Id.ToString(), null, response, cancellationToken);
        return new TopologyMapResult(TopologyMapOutcome.Success, response);
    }

    public async Task<TopologyMapResult> UpdateAsync(
        Guid id,
        SaveTopologyMapRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var map = await Loaded().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (map is null)
        {
            return new TopologyMapResult(TopologyMapOutcome.NotFound);
        }

        var name = request.Name.Trim();
        if (await dbContext.TopologyMaps
            .AnyAsync(other => other.Id != id && other.Name.ToLower() == name.ToLower(), cancellationToken))
        {
            return Duplicate(name);
        }

        if (await UnknownCiAsync(request, cancellationToken) is { } unknown)
        {
            return unknown;
        }

        var before = Map(map);
        map.Name = name;
        map.Description = Trimmed(request.Description);
        map.UpdatedBy = GetActorId(actor);
        map.UpdatedAt = DateTimeOffset.UtcNow;

        // A save is the canvas stating where everything on it now sits, so the result is the request's
        // set of pins and nothing else — a node the operator un-pinned has to be able to disappear, and
        // merging would make un-pinning impossible without a second endpoint for it.
        //
        // The pins that survive are moved rather than deleted and re-inserted. `(map, ci)` is unique,
        // and EF orders an insert before the delete it replaces, so recreating a row for a CI that was
        // already pinned collides with the index on the way through — the same position, refused for
        // being the same position.
        var requested = Distinct(request.Nodes).ToList();
        var wanted = requested.Select(node => node.CiId).ToHashSet();
        foreach (var dropped in map.Nodes.Where(node => !wanted.Contains(node.CiId)).ToList())
        {
            map.Nodes.Remove(dropped);
            dbContext.Remove(dropped);
        }

        var existing = map.Nodes.ToDictionary(node => node.CiId);
        foreach (var node in requested)
        {
            if (existing.TryGetValue(node.CiId, out var pin))
            {
                pin.X = node.X;
                pin.Y = node.Y;
                continue;
            }

            map.Nodes.Add(new TopologyMapNode
            {
                Id = Guid.CreateVersion7(),
                TopologyMapId = map.Id,
                CiId = node.CiId,
                X = node.X,
                Y = node.Y,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(map);
        await auditService.WriteAsync(
            actor, "Updated", "TopologyMap", map.Id.ToString(), before, response, cancellationToken);
        return new TopologyMapResult(TopologyMapOutcome.Success, response);
    }

    public async Task<TopologyMapOutcome> DeleteAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var map = await Loaded().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (map is null)
        {
            return TopologyMapOutcome.NotFound;
        }

        var before = Map(map);
        dbContext.TopologyMaps.Remove(map);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "TopologyMap", id.ToString(), before, null, cancellationToken);
        return TopologyMapOutcome.Success;
    }

    /// <summary>
    /// A pin for a CI that does not exist is a 400 naming it rather than a foreign-key 500. It is the
    /// normal outcome of two people working at once — one deletes a CI while the other is dragging it —
    /// so the message has to be one the browser can act on.
    /// </summary>
    private async Task<TopologyMapResult?> UnknownCiAsync(
        SaveTopologyMapRequest request,
        CancellationToken cancellationToken)
    {
        var requested = request.Nodes.Select(node => node.CiId).Distinct().ToList();
        if (requested.Count == 0)
        {
            return null;
        }

        var known = await dbContext.Cis.AsNoTracking()
            .Where(ci => requested.Contains(ci.Id))
            .Select(ci => ci.Id)
            .ToListAsync(cancellationToken);
        var missing = requested.Except(known).ToArray();
        return missing.Length == 0
            ? null
            : new TopologyMapResult(
                TopologyMapOutcome.UnknownCi,
                Errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(SaveTopologyMapRequest.Nodes)] =
                        [.. missing.Select(ciId => $"CI '{ciId}' does not exist.")],
                });
    }

    /// <summary>
    /// Last position wins for a repeated CI. A canvas cannot put one node in two places, so a request
    /// that does is a client bug rather than an operator's decision — and failing the save would lose
    /// every other position they just arranged.
    /// </summary>
    private static IEnumerable<TopologyMapNodeResponse> Distinct(IReadOnlyList<TopologyMapNodeResponse> nodes) =>
        nodes.GroupBy(node => node.CiId).Select(group => group.Last());

    private IQueryable<TopologyMap> Loaded() => dbContext.TopologyMaps.Include(map => map.Nodes);

    private static TopologyMapResult Duplicate(string name) => new(
        TopologyMapOutcome.DuplicateName,
        Error: $"A topology map named '{name}' already exists.");

    private static TopologyMapResponse Map(TopologyMap map) => new(
        map.Id,
        map.Name,
        map.Description,
        [.. map.Nodes
            .Select(node => new TopologyMapNodeResponse(node.CiId, node.X, node.Y))
            .OrderBy(node => node.CiId)],
        map.CreatedBy,
        map.CreatedAt,
        map.UpdatedBy,
        map.UpdatedAt);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
