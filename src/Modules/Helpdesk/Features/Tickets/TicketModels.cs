using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Tickets;

public sealed record CreateTicketRequest(
    string Title,
    string Description,
    TicketType Type,
    TicketLevel Urgency,
    TicketLevel Impact,
    string? RequesterId);

public sealed record UpdateTicketRequest(
    string Title,
    string Description,
    TicketType Type,
    TicketLevel Urgency,
    TicketLevel Impact);

public sealed record TicketResponse(
    Guid Id,
    string Number,
    string Title,
    string Description,
    TicketType Type,
    TicketLevel Urgency,
    TicketLevel Impact,
    TicketPriority Priority,
    string RequesterId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TicketPageResponse(
    IReadOnlyList<TicketResponse> Items,
    int Total,
    int Page,
    int PageSize);
