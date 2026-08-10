using Contracts.Events;

using MassTransit;

using Microsoft.Extensions.Logging;

using Platform.Messaging;

namespace Modules.Monitoring.Features.Metrics;

/// <summary>
/// The platform's half of WP-3.3's telemetry: everything a poller measured in one cycle becomes rows
/// in the metrics hypertable. Binding this consumer is the whole of "ingestion turns on" — until
/// WP-3.4 the telemetry exchange was a fanout with nothing bound to it, so the poller needs no change
/// and starts being listened to the moment this ships.
/// <para>
/// Idempotent through the Platform dedupe helper, unlike WP-3.2's heartbeat consumer, which is
/// idempotent by construction. The two are different shapes: a heartbeat is one forward-only column
/// update that is safe to repeat, while this writes N rows across two DbContexts and can therefore
/// fail half way. The dedupe row costs one row per poller cycle; the natural key on the hypertable
/// makes the insert itself a no-op on replay, which is what covers the window between the two
/// transactions.
/// </para>
/// </summary>
public sealed class DeviceTelemetryConsumer(
    IConsumerIdempotencyService idempotencyService,
    IMetricIngestionService ingestionService,
    ILogger<DeviceTelemetryConsumer> logger) : IConsumer<DeviceTelemetryReported>
{
    public async Task Consume(ConsumeContext<DeviceTelemetryReported> context)
    {
        var telemetry = context.Message;
        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"device-telemetry:{telemetry.EventId}",
            async cancellationToken =>
            {
                var written = await ingestionService.IngestAsync(telemetry, cancellationToken);
                logger.LogDebug(
                    "Stored {MetricCount} metrics from poller {PollerName} cycle {CycleNumber}.",
                    written,
                    telemetry.PollerName,
                    telemetry.CycleNumber);
            },
            context.CancellationToken);

        if (!accepted)
        {
            logger.LogDebug(
                "Telemetry {EventId} from poller {PollerName} was already ingested; skipped.",
                telemetry.EventId,
                telemetry.PollerName);
        }
    }
}
