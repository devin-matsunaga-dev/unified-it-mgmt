using Modules.Helpdesk.Features.Tickets;

namespace Modules.Helpdesk.Features.Views;

public sealed record SaveTicketViewRequest(string Name, bool IsShared, TicketListFilter Filter);

public sealed record TicketViewResponse(
    Guid Id,
    string Name,
    string OwnerId,
    string OwnerName,
    bool IsShared,
    bool IsMine,
    TicketListFilter Filter,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
