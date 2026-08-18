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
    /// <summary>
    /// Whether this actor may delete it: their own, or anybody's if they are an Admin. A shared view
    /// used to be deletable only by its owner, so one left behind by somebody who has moved on could
    /// never be removed by anyone.
    /// </summary>
    bool CanDelete,
    TicketListFilter Filter,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
