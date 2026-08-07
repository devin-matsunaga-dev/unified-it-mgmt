using System.Security.Claims;

namespace Modules.Helpdesk.Features.Tickets;

public interface ITicketService
{
    Task<TicketResponse?> CreateAsync(
        CreateTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TicketResponse?> GetAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<TicketPageResponse> ListAsync(
        int page,
        int pageSize,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TicketResponse?> UpdateAsync(
        Guid id,
        UpdateTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TransitionTicketResult> TransitionAsync(
        Guid id,
        TransitionTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TicketTransitionResponse>?> GetTransitionHistoryAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}

public enum TransitionTicketOutcome
{
    Success,
    NotFound,
    UnknownStatus,
    IllegalTransition,
    ResolutionNoteRequired,
    Forbidden,
}

public sealed record TransitionTicketResult(
    TransitionTicketOutcome Outcome,
    TicketResponse? Ticket = null,
    string? Error = null);
