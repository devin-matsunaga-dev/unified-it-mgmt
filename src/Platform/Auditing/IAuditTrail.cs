namespace Platform.Auditing;

/// <summary>
/// The read side of the audit log (WP-5.3). Separate from <see cref="IAuditService"/> on purpose: that
/// one is the invariant-bearing write path every endpoint in the platform goes through, and widening it
/// with a query would put a read surface on a service whose whole job is that nothing writes without it.
/// <para>
/// Not a port. <c>platform.audit_entries</c> is Platform's own table and every module already references
/// Platform to write to it, so a module asking what it wrote is an ordinary service call rather than a
/// cross-module read (ARCHITECTURE §3 — a port exists for the case where neither side may reference the
/// other, which is not this one).
/// </para>
/// </summary>
public interface IAuditTrail
{
    /// <summary>
    /// What was recorded against one entity, newest first, capped at <paramref name="limit"/>.
    /// </summary>
    /// <param name="excludeActions">
    /// Actions the caller already renders from a better source. A CI's lifecycle move is written both to
    /// <c>assets.ci_lifecycle_history</c>, with its from-state and its note, and to the audit log as a
    /// whole-entity before/after — and a timeline that read both would say the same thing twice, once
    /// well and once badly. Excluded here rather than after the read so the cap is not spent on rows the
    /// caller is going to throw away.
    /// </param>
    Task<AuditTrail> GetForEntityAsync(
        string entityType,
        string entityId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyCollection<string>? excludeActions,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>The entries to render, and how many the window really holds behind the cap.</summary>
public sealed record AuditTrail(IReadOnlyList<AuditTrailEntry> Entries, int Total);

/// <summary>
/// One audit row, with the before/after documents left as raw JSON.
/// <para>
/// Deliberately not deserialised here: the audit log stores whatever the writing module handed it, so
/// Platform has no type to read it back into and inventing one would make the log's shape Platform's
/// business. The caller compares the two documents; nothing else in the platform has ever needed to.
/// </para>
/// </summary>
public sealed record AuditTrailEntry(
    Guid Id,
    string ActorId,
    string Action,
    string? BeforeJson,
    string? AfterJson,
    DateTimeOffset OccurredAt,
    string CorrelationId);
