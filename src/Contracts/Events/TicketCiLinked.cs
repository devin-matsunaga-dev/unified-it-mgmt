namespace Contracts.Events;

public sealed record TicketCiLinked(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TicketId,
    string TicketNumber,
    Guid CiId,
    string ActorId);
