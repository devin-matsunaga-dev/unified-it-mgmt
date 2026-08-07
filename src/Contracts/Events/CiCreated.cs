namespace Contracts.Events;

public sealed record CiCreated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CiId,
    string CiType,
    string Name,
    string? AssetTag);
