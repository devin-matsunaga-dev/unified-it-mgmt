namespace Contracts.Events;

public sealed record SlaBreached(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TicketId,
    string TicketNumber,
    string Target,
    DateTimeOffset DueAt);
