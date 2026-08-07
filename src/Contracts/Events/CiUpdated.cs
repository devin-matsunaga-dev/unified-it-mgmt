namespace Contracts.Events;

public sealed record CiUpdated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CiId,
    string CiType,
    string Name,
    bool IsActive);
