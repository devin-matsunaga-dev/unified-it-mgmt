using Microsoft.EntityFrameworkCore;
using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.PollerConfig;

/// <summary>
/// Allocates configuration versions and records what changed at each one. Every write in this module
/// goes through here inside its own transaction, which is what lets a poller ask "what changed since
/// version N" and get an answer that includes deletions.
/// </summary>
public interface IMonitoringConfigLog
{
    /// <summary>
    /// Records a change and returns the version it was given. Must be called inside a transaction —
    /// the advisory lock it takes is transaction-scoped, and it is the lock that makes versions
    /// commit in the order they were issued.
    /// </summary>
    Task<long> RecordAsync(
        MonitoringConfigEntity entityType,
        Guid entityId,
        Guid? deviceId,
        string? pollerGroup,
        MonitoringConfigChangeKind kind,
        CancellationToken cancellationToken);

    /// <summary>The newest version anyone has been given, or 0 against an untouched database.</summary>
    Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken);
}

public sealed class MonitoringConfigLog(MonitoringDbContext dbContext) : IMonitoringConfigLog
{
    /// <summary>
    /// An arbitrary but fixed key. Everything that allocates a monitoring config version contends on
    /// this one lock; config edits are operator-paced, so serialising them costs nothing.
    /// </summary>
    private const long VersionLockKey = 8_314_071_031L;

    public async Task<long> RecordAsync(
        MonitoringConfigEntity entityType,
        Guid entityId,
        Guid? deviceId,
        string? pollerGroup,
        MonitoringConfigChangeKind kind,
        CancellationToken cancellationToken)
    {
        // Held until the caller's transaction ends, so no two writers can interleave the read of the
        // maximum version with each other's insert.
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({VersionLockKey})",
            cancellationToken);

        var version = await GetCurrentVersionAsync(cancellationToken) + 1;
        dbContext.ConfigChanges.Add(new MonitoringConfigChange
        {
            Version = version,
            EntityType = entityType,
            EntityId = entityId,
            DeviceId = deviceId,
            PollerGroup = pollerGroup,
            Kind = kind,
            OccurredAt = DateTimeOffset.UtcNow,
        });

        return version;
    }

    public async Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken) =>
        await dbContext.ConfigChanges
            .Select(change => (long?)change.Version)
            .MaxAsync(cancellationToken) ?? 0L;
}
