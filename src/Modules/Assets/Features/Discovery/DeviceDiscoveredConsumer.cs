using Contracts.Events;

using MassTransit;

using Microsoft.Extensions.Logging;

using Platform.Messaging;

namespace Modules.Assets.Features.Discovery;

/// <summary>
/// The first consumer of <see cref="DeviceDiscovered"/>, and the reason WP-4.1's scanner was worth
/// building: it turns "an address answered" into either a CI that is now current or a card somebody has
/// to look at.
/// <para>
/// It lives in Assets because its output is a CI, and CIs are Assets' (ARCHITECTURE §3). The consumer
/// itself does nothing but idempotency and logging — the placement is
/// <see cref="IDiscoveryReviewService.IngestAsync"/>, which takes the contract event directly so that
/// the test reading WP-4.1's committed envelope fixture exercises the same path a broker does.
/// </para>
/// </summary>
public sealed class DeviceDiscoveredConsumer(
    IConsumerIdempotencyService idempotencyService,
    IDiscoveryReviewService reviewService,
    ILogger<DeviceDiscoveredConsumer> logger) : IConsumer<DeviceDiscovered>
{
    public async Task Consume(ConsumeContext<DeviceDiscovered> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var discovery = context.Message;

        // Keyed on the event, not on the device. A redelivery of one message must not count as a second
        // sighting — but the *next scan* reporting the same device is a new event and must be processed,
        // because that is what moves last-seen and keeps the CI current.
        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"device-discovered:{discovery.EventId}",
            cancellationToken => reviewService.IngestAsync(discovery, cancellationToken),
            context.CancellationToken);
        if (!accepted)
        {
            logger.LogDebug(
                "DeviceDiscovered {EventId} for {Address} was already ingested; skipped.",
                discovery.EventId, discovery.Address);
        }
    }
}
