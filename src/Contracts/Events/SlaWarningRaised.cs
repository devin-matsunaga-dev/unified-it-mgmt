namespace Contracts.Events;

public sealed record SlaWarningRaised(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TicketId,
    string TicketNumber,
    string Target,
    DateTimeOffset DueAt);
