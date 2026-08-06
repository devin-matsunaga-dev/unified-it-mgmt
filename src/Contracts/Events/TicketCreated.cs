namespace Contracts.Events;

public sealed record TicketCreated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TicketId,
    string Number,
    string RequesterId,
    string Type,
    string Priority);
