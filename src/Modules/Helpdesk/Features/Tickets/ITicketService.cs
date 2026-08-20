using System.Security.Claims;

namespace Modules.Helpdesk.Features.Tickets;

public interface ITicketService
{
    Task<TicketWriteResult> CreateAsync(
        CreateTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TicketResponse?> GetAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<TicketListResult> ListAsync(
        TicketListFilter filter,
        int page,
        int pageSize,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TicketWriteResult> UpdateAsync(
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

public enum TicketWriteOutcome
{
    Success,
    TicketNotFound,
    QueueNotFound,
    CategoryNotFound,
    InvalidCustomFields,
    CiNotFound,
}

public sealed record TicketWriteResult(
    TicketWriteOutcome Outcome,
    TicketResponse? Ticket = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

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
