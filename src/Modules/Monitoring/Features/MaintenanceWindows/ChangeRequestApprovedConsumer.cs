using System.Security.Claims;

using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

using Platform.Messaging;

namespace Modules.Monitoring.Features.MaintenanceWindows;

public interface IMaintenanceSyncService
{
    Task SyncAsync(ChangeRequestApproved approval, CancellationToken cancellationToken);
}

/// <summary>
/// The monitoring half of approving a change (WP-5.8): the CIs somebody agreed could be disturbed become
/// a maintenance window over whichever of them this module actually polls.
/// <para>
/// It reacts to an event rather than being called, because Assets owns change requests and Monitoring
/// owns windows and neither module may reference the other. ARCHITECTURE §3 says a port is a read surface
/// and never a write path, and opening a window is a write — the same arrangement, for the same reason,
/// as <c>DiscoveredDeviceApprovedConsumer</c>.
/// </para>
/// <para>
/// Split from its consumer, following that precedent: the consumer's job is idempotency and the service's
/// job is the work, and only one of the two is worth testing against a real bus.
/// </para>
/// </summary>
public sealed class MaintenanceSyncService(
    MonitoringDbContext dbContext,
    IMaintenanceWindowService windowService,
    ILogger<MaintenanceSyncService> logger) : IMaintenanceSyncService
{
    /// <summary>
    /// Neither an agent nor a poller. The window is written by the platform on an operator's decision, and
    /// the audit row says so rather than naming the person who pressed approve — they approved a change,
    /// which is the act that is theirs; the window is a consequence. WP-3.2's system actor, and WP-4.2's.
    /// </summary>
    private static readonly ClaimsPrincipal SystemActor = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "system:change-approval"),
            new Claim(ClaimTypes.Name, "Change approval"),
        ],
        "ChangeApproval"));

    public async Task SyncAsync(ChangeRequestApproved approval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);

        var ciIds = approval.CiIds ?? [];
        var deviceIds = ciIds.Count == 0
            ? []
            : await dbContext.MonitoredDevices
                .Where(device => ciIds.Contains(device.CiId))
                .Select(device => device.Id)
                .ToListAsync(cancellationToken);

        if (deviceIds.Count == 0)
        {
            // Most of a CMDB is not polled — laptops, licences, logical services — so a change that
            // covers nothing monitored is the ordinary case rather than an error, and it is emphatically
            // not a reason to fall back to an estate-wide window. Nothing is muted and nothing is
            // recorded, which is exactly what the change asked for.
            logger.LogInformation(
                "Change {Number} ({ChangeRequestId}) covers {CiCount} CI(s), none of them monitored; "
                + "no maintenance window was opened.",
                approval.Number, approval.ChangeRequestId, ciIds.Count);
            return;
        }

        var result = await windowService.CreateForChangeAsync(
            approval.ChangeRequestId,
            new CreateMaintenanceWindowRequest(
                Name: $"{approval.Number} — {approval.Title}",
                StartsAt: approval.StartsAt,
                EndsAt: approval.EndsAt,
                Description: $"Opened automatically by approved change {approval.Number}.",
                AppliesToAllDevices: false,
                DeviceIds: deviceIds,
                IsActive: true),
            SystemActor,
            cancellationToken);

        switch (result.Outcome)
        {
            case MonitoringOutcome.Success:
                logger.LogInformation(
                    "Change {Number} opened maintenance window {WindowId} over {DeviceCount} device(s) "
                    + "from {StartsAt:u} to {EndsAt:u}.",
                    approval.Number, result.Window!.Id, deviceIds.Count, approval.StartsAt, approval.EndsAt);
                return;

            case MonitoringOutcome.Duplicate:
                // A window for this change already exists, so the approval has been delivered before.
                // Logged and dropped rather than thrown, so a redelivery does not loop forever.
                logger.LogInformation(
                    "Change {Number} already has maintenance window {WindowId}; nothing was opened.",
                    approval.Number, result.Window!.Id);
                return;

            default:
                // Anything else is a genuine refusal — a device deleted between the query and the write,
                // an end that is not after its start — and it throws so the message is retried and,
                // failing that, lands where a human will see it. A change believed to be muting an
                // estate that is not must never be silent.
                throw new InvalidOperationException(
                    $"Opening a maintenance window for change {approval.Number} "
                    + $"({approval.ChangeRequestId}) failed: {result.Outcome}.");
        }
    }
}

public sealed class ChangeRequestApprovedConsumer(
    IConsumerIdempotencyService idempotencyService,
    IMaintenanceSyncService syncService,
    ILogger<ChangeRequestApprovedConsumer> logger) : IConsumer<ChangeRequestApproved>
{
    public async Task Consume(ConsumeContext<ChangeRequestApproved> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var approval = context.Message;

        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"change-request-approved:{approval.EventId}",
            cancellationToken => syncService.SyncAsync(approval, cancellationToken),
            context.CancellationToken);
        if (!accepted)
        {
            logger.LogDebug(
                "ChangeRequestApproved {EventId} was already synced; skipped.", approval.EventId);
        }
    }
}
