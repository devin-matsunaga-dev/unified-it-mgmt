using Microsoft.EntityFrameworkCore;

using Platform.Data;

namespace Platform.Auditing;

/// <summary>
/// Reads <c>platform.audit_entries</c> back. The write side guards the table as append-only
/// (<c>PlatformDbContext.GuardAppendOnlyAuditEntries</c>), and there is nothing here that could change
/// that: the query is <c>AsNoTracking</c> and returns records rather than entities.
/// </summary>
public sealed class AuditTrailReader(PlatformDbContext dbContext) : IAuditTrail
{
    /// <summary>The most rows one call will return, whatever the caller asks for.</summary>
    internal const int MaximumLimit = 200;

    public async Task<AuditTrail> GetForEntityAsync(
        string entityType,
        string entityId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyCollection<string>? excludeActions,
        int limit,
        CancellationToken cancellationToken)
    {
        var excluded = excludeActions?.ToArray() ?? [];
        var matching = dbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.EntityType == entityType && entry.EntityId == entityId)
            .Where(entry => !excluded.Contains(entry.Action));

        if (from is not null)
        {
            matching = matching.Where(entry => entry.OccurredAt >= from);
        }

        if (to is not null)
        {
            matching = matching.Where(entry => entry.OccurredAt <= to);
        }

        // Counted over the same predicate rather than off the returned page, so a capped timeline can
        // still state the honest total — WP-2.4's rule.
        var total = await matching.CountAsync(cancellationToken);

        var entries = await matching
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Take(Math.Clamp(limit, 1, MaximumLimit))
            .Select(entry => new AuditTrailEntry(
                entry.Id,
                entry.ActorId,
                entry.Action,
                entry.BeforeJson,
                entry.AfterJson,
                entry.OccurredAt,
                entry.CorrelationId))
            .ToListAsync(cancellationToken);

        return new(entries, total);
    }
}
