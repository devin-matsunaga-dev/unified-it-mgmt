using System.Security.Claims;

using Contracts.Events;

using MassTransit;

using Microsoft.Extensions.Logging;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

using Platform.Messaging;

namespace Modules.Monitoring.Features.Discovery;

public interface IDiscoveryEnrollmentService
{
    Task EnrollAsync(DiscoveredDeviceApproved approval, CancellationToken cancellationToken);
}

/// <summary>
/// The monitoring half of approving a discovery: when the approver ticked "monitor this", the CI they
/// just created becomes a monitored device with a reachability check.
/// <para>
/// It reacts to an event rather than being called, because Assets owns CIs and Monitoring owns devices
/// and neither module may reference the other. The read direction has a port
/// (<see cref="Platform.Integration.IMonitoredAddressDirectory"/>); the write direction has to be an
/// event, because ARCHITECTURE §3 says a port is never a write path.
/// </para>
/// <para>
/// Split from its consumer for the reason <c>AlertNotificationService</c> is: the consumer's job is
/// idempotency and the service's job is the work, and only one of those is worth testing against real
/// infrastructure.
/// </para>
/// </summary>
public sealed class DiscoveryEnrollmentService(
    IMonitoredDeviceService deviceService,
    ILogger<DiscoveryEnrollmentService> logger) : IDiscoveryEnrollmentService
{
    /// <summary>
    /// Neither an agent nor a poller. The device and its check are written by the platform on an
    /// operator's instruction, and every audit row it leaves says so rather than naming a person who
    /// only pressed approve.
    /// </summary>
    private static readonly ClaimsPrincipal SystemActor = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "system:discovery"),
            new Claim(ClaimTypes.Name, "Discovery"),
        ],
        "Discovery"));

    /// <summary>
    /// The one check an enrolment can create without inventing configuration. Every other type needs a
    /// parameter nobody has chosen — <c>CheckRules.RequiredParameter</c> names an OID, a port or a URL
    /// for each — and a discovered stranger has no vault credential either. A device with no checks is
    /// polled by nobody and appears on no board, so stopping at the device would be enrolment in name
    /// only.
    /// </summary>
    private const int ReachabilityIntervalSeconds = 60;
    private const int ReachabilityTimeoutSeconds = 5;

    public async Task EnrollAsync(DiscoveredDeviceApproved approval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var pollerGroup = string.IsNullOrWhiteSpace(approval.PollerGroup) ? null : approval.PollerGroup.Trim();
        var device = await deviceService.CreateAsync(
            new CreateMonitoredDeviceRequest(
                approval.CiId,
                approval.Address,
                pollerGroup,
                IsEnabled: true,
                Notes: $"Enrolled from discovery {approval.DiscoveredDeviceId}."),
            SystemActor,
            cancellationToken);

        if (device.Outcome is MonitoringOutcome.Duplicate)
        {
            // One CI is monitored at most once (WP-3.1's unique index). Approving something already
            // monitored is not an error — it is the estate agreeing with the operator — so this is
            // logged and dropped rather than thrown and redelivered forever.
            logger.LogInformation(
                "CI {CiId} is already monitored; the approval of {DiscoveredDeviceId} enrolled nothing.",
                approval.CiId, approval.DiscoveredDeviceId);
            return;
        }

        if (device.Outcome is not MonitoringOutcome.Success)
        {
            throw new InvalidOperationException(
                $"Enrolling CI {approval.CiId} from discovery {approval.DiscoveredDeviceId} failed: {device.Outcome}.");
        }

        var check = await deviceService.CreateCheckAsync(
            device.Device!.Id,
            new CreateCheckRequest(
                CheckType.Icmp,
                "Reachability",
                ReachabilityIntervalSeconds,
                ReachabilityTimeoutSeconds),
            SystemActor,
            cancellationToken);
        if (check.Outcome is not MonitoringOutcome.Success)
        {
            throw new InvalidOperationException(
                $"Enrolled device {device.Device.Id} but its reachability check failed: {check.Outcome}.");
        }

        logger.LogInformation(
            "Discovered device {DiscoveredDeviceId} enrolled CI {CiId} as monitored device {DeviceId} at {Address}.",
            approval.DiscoveredDeviceId, approval.CiId, device.Device.Id, approval.Address);
    }
}

public sealed class DiscoveredDeviceApprovedConsumer(
    IConsumerIdempotencyService idempotencyService,
    IDiscoveryEnrollmentService enrollmentService,
    ILogger<DiscoveredDeviceApprovedConsumer> logger) : IConsumer<DiscoveredDeviceApproved>
{
    public async Task Consume(ConsumeContext<DiscoveredDeviceApproved> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var approval = context.Message;

        // Approvals that asked for no monitoring are still published — the CI is a fact either way —
        // and this consumer is simply not what they are for. Returning before the idempotency helper
        // keeps a dedupe row from being written for every approval in the estate.
        if (!approval.MonitoringRequested)
        {
            logger.LogDebug(
                "Discovered device {DiscoveredDeviceId} was approved without monitoring; nothing enrolled.",
                approval.DiscoveredDeviceId);
            return;
        }

        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"discovered-device-approved:{approval.EventId}",
            cancellationToken => enrollmentService.EnrollAsync(approval, cancellationToken),
            context.CancellationToken);
        if (!accepted)
        {
            logger.LogDebug(
                "DiscoveredDeviceApproved {EventId} was already enrolled; skipped.", approval.EventId);
        }
    }
}
