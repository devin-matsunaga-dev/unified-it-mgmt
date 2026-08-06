namespace Contracts.Events;

public sealed record TicketUpdated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TicketId,
    string Number,
    string RequesterId,
    string Type,
    string Priority);
