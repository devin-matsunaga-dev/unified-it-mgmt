using System.Security.Claims;

namespace Modules.Helpdesk.Features.TicketCis;

/// <summary>
/// The CIs a ticket is about. Helpdesk owns the link rows (ARCHITECTURE §3) and reads the CI itself
/// through the Assets port.
/// </summary>
public interface ITicketCiLinkService
{
    Task<TicketCiLinkListResult> ListAsync(Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<TicketCiLinkResult> LinkAsync(
        Guid ticketId,
        LinkTicketCiRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TicketCiLinkOutcome> UnlinkAsync(
        Guid ticketId,
        Guid ciId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
