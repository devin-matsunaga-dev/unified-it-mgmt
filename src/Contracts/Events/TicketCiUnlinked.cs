namespace Contracts.Events;

public sealed record TicketCiUnlinked(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TicketId,
    string TicketNumber,
    Guid CiId,
    string ActorId);
