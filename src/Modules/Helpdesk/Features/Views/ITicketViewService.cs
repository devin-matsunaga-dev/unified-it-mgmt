using System.Security.Claims;

namespace Modules.Helpdesk.Features.Views;

public interface ITicketViewService
{
    Task<IReadOnlyList<TicketViewResponse>> ListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<TicketViewResult> CreateAsync(
        SaveTicketViewRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TicketViewResult> UpdateAsync(
        Guid id,
        SaveTicketViewRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TicketViewOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);
}

public enum TicketViewOutcome
{
    Success,
    NotFound,
    DuplicateName,
    Forbidden,
}

public sealed record TicketViewResult(
    TicketViewOutcome Outcome,
    TicketViewResponse? View = null,
    string? Error = null);
