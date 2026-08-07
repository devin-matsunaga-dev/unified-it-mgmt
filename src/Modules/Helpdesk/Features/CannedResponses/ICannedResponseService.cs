using System.Security.Claims;

namespace Modules.Helpdesk.Features.CannedResponses;

public interface ICannedResponseService
{
    Task<IReadOnlyList<CannedResponseResponse>> ListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<CannedResponseResult> CreateAsync(
        SaveCannedResponseRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CannedResponseResult> UpdateAsync(
        Guid id,
        SaveCannedResponseRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CannedResponseOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<RenderResult> RenderAsync(
        Guid id,
        RenderCannedResponseRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}

public enum CannedResponseOutcome
{
    Success,
    NotFound,
    TicketNotFound,
    DuplicateName,
    Forbidden,
}

public sealed record CannedResponseResult(
    CannedResponseOutcome Outcome,
    CannedResponseResponse? Response = null,
    string? Error = null);

public sealed record RenderResult(CannedResponseOutcome Outcome, RenderedCannedResponse? Rendered = null);
