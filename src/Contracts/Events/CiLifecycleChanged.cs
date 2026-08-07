namespace Contracts.Events;

public sealed record CiLifecycleChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CiId,
    string CiType,
    string FromState,
    string ToState,
    string ActorId);
