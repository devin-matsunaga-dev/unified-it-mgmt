namespace Contracts.Events;

public sealed record CiRelationshipRemoved(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid RelationshipId,
    Guid SourceCiId,
    Guid TargetCiId,
    string RelationshipType,
    string ActorId);
