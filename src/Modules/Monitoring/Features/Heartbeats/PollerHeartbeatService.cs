using System.Security.Claims;

using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;

using Platform.Auditing;

namespace Modules.Monitoring.Features.Heartbeats;

public interface IPollerHeartbeatService
{
    /// <summary>Applies one heartbeat. Returns false when it was ignored — see the implementation.</summary>
    Task<bool> RecordAsync(PollerHeartbeat heartbeat, CancellationToken cancellationToken);

    /// <summary>Reports every poller that has gone quiet since the last pass. Returns how many.</summary>
    Task<int> EvaluateAsync(CancellationToken cancellationToken);
}

public sealed class PollerHeartbeatService(
    MonitoringDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService,
    IOptions<PollerHeartbeatOptions> options,
    ILogger<PollerHeartbeatService> logger) : IPollerHeartbeatService
{
    private static readonly ClaimsPrincipal SystemActor = new(new ClaimsIdentity(
        [new Claim("sub", "system:monitoring")],
        "Monitoring"));

    /// <summary>
    /// Idempotent by construction rather than by a dedupe row: the stored heartbeat only ever moves
    /// forward, so a redelivered beat and one that overtook its predecessor are both no-ops. That is
    /// deliberate — a dedupe row per beat is a row every fifteen seconds per poller, forever, to
    /// protect an update that is already safe to repeat.
    /// </summary>
    public async Task<bool> RecordAsync(PollerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        var name = heartbeat.PollerName.Trim();
        var poller = await dbContext.Pollers.SingleOrDefaultAsync(item => item.Name == name, cancellationToken);
        if (poller is null)
        {
            // A heartbeat from a poller that never registered is dropped rather than creating one:
            // registration is an authenticated statement about a poller's group, and a message on a
            // queue is not the place to make it.
            logger.LogWarning("Heartbeat from unregistered poller {PollerName} ignored.", name);
            return false;
        }

        // Truncated to what Postgres will keep, on both sides of the comparison. A DateTimeOffset
        // carries 100ns ticks and a timestamptz keeps microseconds, so comparing the incoming value
        // against the stored one at full precision makes a message look newer than itself once it
        // has been through the database — which is exactly the redelivery this guard exists to
        // absorb.
        var occurredAt = ToStoredPrecision(heartbeat.OccurredAt);
        if (poller.LastHeartbeatAt is { } last && occurredAt <= ToStoredPrecision(last))
        {
            return false;
        }

        poller.LastHeartbeatAt = occurredAt;
        poller.HeartbeatIntervalSeconds = heartbeat.IntervalSeconds;
        poller.LastCycleNumber = heartbeat.CycleNumber;
        poller.LastReportedDeviceCount = heartbeat.DeviceCount;
        if (heartbeat.AgentVersion is { Length: > 0 } agentVersion)
        {
            poller.AgentVersion = agentVersion;
        }

        // The poller is back, so the next silence is a new one and gets its own event.
        poller.HeartbeatMissedAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Rounds down to the microsecond a <c>timestamptz</c> column will store.</summary>
    private static DateTimeOffset ToStoredPrecision(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % (TimeSpan.TicksPerMillisecond / 1_000)), value.Offset);

    public async Task<int> EvaluateAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var candidates = await dbContext.Pollers
            .Where(poller => poller.IsEnabled && poller.LastHeartbeatAt != null && poller.HeartbeatMissedAt == null)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var silent = PollerHeartbeatEvaluator.Plan(
            candidates, now, settings.MissedThreshold, settings.DefaultIntervalSeconds);
        if (silent.Count == 0)
        {
            return 0;
        }

        foreach (var entry in silent)
        {
            entry.Poller.HeartbeatMissedAt = now;
        }

        // Marked first: if the publish fails, the pass has still recorded that this silence was seen,
        // and the alternative — publishing first — risks a message for an outage no row remembers.
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var entry in silent)
        {
            var poller = entry.Poller;
            logger.LogWarning(
                "Poller {PollerName} has missed {MissedHeartbeats} heartbeats; last seen {LastHeartbeatAt:o}.",
                poller.Name,
                entry.MissedHeartbeats,
                poller.LastHeartbeatAt);

            await publishEndpoint.Publish(
                new PollerHeartbeatMissed(
                    Guid.CreateVersion7(),
                    now,
                    poller.Id,
                    poller.Name,
                    poller.PollerGroup,
                    poller.LastHeartbeatAt!.Value,
                    entry.MissedHeartbeats,
                    entry.IntervalSeconds),
                cancellationToken);

            // Also the flush: the outbox lives on the Platform context, and writing the audit entry
            // is what commits both. A poller going quiet is a fact an operator will want dated.
            await auditService.WriteAsync(
                SystemActor,
                "HeartbeatMissed",
                "Poller",
                poller.Id.ToString(),
                before: null,
                after: new
                {
                    poller.Name,
                    poller.PollerGroup,
                    poller.LastHeartbeatAt,
                    entry.MissedHeartbeats,
                    entry.IntervalSeconds,
                },
                cancellationToken);
        }

        return silent.Count;
    }
}
