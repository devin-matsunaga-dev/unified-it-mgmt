namespace Contracts.Events;

public sealed record CiAssignmentChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CiId,
    string CiType,
    string Action,
    Guid? OwnerUserId,
    Guid? DepartmentId,
    Guid? SiteId,
    string ActorId);
