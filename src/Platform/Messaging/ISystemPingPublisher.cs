using System.Security.Claims;

namespace Platform.Messaging;

public interface ISystemPingPublisher
{
    Task<PublishedSystemPing> PublishAsync(
        string dedupeKey,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);
}

public sealed record PublishedSystemPing(Guid EventId, string DedupeKey);
