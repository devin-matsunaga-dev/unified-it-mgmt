namespace Contracts.Events;

public sealed record CiRelationshipCreated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid RelationshipId,
    Guid SourceCiId,
    Guid TargetCiId,
    string RelationshipType,
    string ActorId);
