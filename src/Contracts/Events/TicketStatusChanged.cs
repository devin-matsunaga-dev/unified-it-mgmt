namespace Contracts.Events;

public sealed record TicketStatusChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TicketId,
    string Number,
    string FromStatus,
    string ToStatus,
    string ActorId);
