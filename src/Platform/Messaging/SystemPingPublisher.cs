using System.Security.Claims;

using Contracts.Events;

using MassTransit;

using Platform.Auditing;

namespace Platform.Messaging;

public sealed class SystemPingPublisher(
    IPublishEndpoint publisher,
    IAuditService auditService) : ISystemPingPublisher
{
    public async Task<PublishedSystemPing> PublishAsync(
        string dedupeKey,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var ping = new SystemPing(Guid.CreateVersion7(), DateTimeOffset.UtcNow, dedupeKey);
        await publisher.Publish(ping, cancellationToken);
        await auditService.WriteAsync(
            actor,
            "Published",
            nameof(SystemPing),
            ping.EventId.ToString(),
            before: null,
            after: new { ping.DedupeKey },
            cancellationToken);

        return new PublishedSystemPing(ping.EventId, ping.DedupeKey);
    }
}
