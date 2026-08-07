namespace Contracts.Events;

public sealed record CiDeleted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CiId,
    string CiType);
