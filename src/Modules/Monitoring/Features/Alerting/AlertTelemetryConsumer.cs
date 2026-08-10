using Contracts.Events;

using MassTransit;

using Microsoft.Extensions.Logging;

using Platform.Messaging;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// The second consumer of <see cref="DeviceTelemetryReported"/>, beside WP-3.4's ingestion. They are
/// separate on purpose: storing a reading and judging it are different jobs with different failure
/// modes, and MassTransit gives each consumer its own queue, so an alert engine that faults does not
/// stop metrics being written — or the other way round.
/// <para>
/// Deduped through the Platform helper, which this one genuinely needs: the state machine counts
/// consecutive readings, so a redelivered batch would advance every "for N cycles" counter a second
/// time and could raise an alert a cycle early. That is the opposite of WP-3.2's heartbeat, which is
/// safe to repeat by construction.
/// </para>
/// </summary>
public sealed class AlertTelemetryConsumer(
    IConsumerIdempotencyService idempotencyService,
    IAlertEngine engine,
    ILogger<AlertTelemetryConsumer> logger) : IConsumer<DeviceTelemetryReported>
{
    public async Task Consume(ConsumeContext<DeviceTelemetryReported> context)
    {
        var telemetry = context.Message;
        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"alert-telemetry:{telemetry.EventId}",
            async cancellationToken =>
            {
                var changes = await engine.EvaluateAsync(telemetry, cancellationToken);
                if (changes > 0)
                {
                    logger.LogInformation(
                        "Cycle {CycleNumber} from poller {PollerName} changed {ChangeCount} alerts.",
                        telemetry.CycleNumber, telemetry.PollerName, changes);
                }
            },
            context.CancellationToken);

        if (!accepted)
        {
            logger.LogDebug(
                "Telemetry {EventId} from poller {PollerName} was already evaluated; skipped.",
                telemetry.EventId, telemetry.PollerName);
        }
    }
}
