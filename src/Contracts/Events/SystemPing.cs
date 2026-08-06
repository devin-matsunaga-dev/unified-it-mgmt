namespace Contracts.Events;

public sealed record SystemPing(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string DedupeKey);
