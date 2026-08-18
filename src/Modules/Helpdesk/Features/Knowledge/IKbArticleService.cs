using System.Security.Claims;

namespace Modules.Helpdesk.Features.Knowledge;

/// <summary>
/// The knowledge base (WP-5.9), read by three different people: an agent writing one, an agent typing a
/// ticket, and an end user about to raise one.
/// <para>
/// Every read here takes the actor rather than a "published only" flag. The flag would be a caller's
/// promise; the actor is the rule — an end user reads published articles and nothing else, narrowed inside
/// the query, which is WP-1.8's line and the same one <c>TicketViewService</c> holds for tickets.
/// </para>
/// </summary>
public interface IKbArticleService
{
    Task<KbArticlePageResponse> ListAsync(
        KbArticleListFilter filter,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<KbArticleResponse?> GetAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<KbArticleResult> CreateAsync(
        CreateKbArticleRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<KbArticleResult> UpdateAsync(
        Guid id,
        UpdateKbArticleRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<KbArticleResult> TransitionAsync(
        Guid id,
        KbTransitionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Puts an earlier version's content back as the current one, as a new version rather than by rewinding
    /// the number — the history is a record and must not be editable by using it.
    /// </summary>
    Task<KbArticleResult> RestoreAsync(
        Guid id,
        int version,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Articles like this text, ranked. Published only, for everybody: a draft surfacing in an agent's
    /// suggestions would put half-written advice on a ticket, which is the failure the draft state exists
    /// to prevent.
    /// </summary>
    Task<IReadOnlyList<KbSuggestionResponse>> SuggestAsync(
        KbSuggestionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TicketKbArticleResponse>> ListForTicketAsync(
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TicketKbArticleResult> LinkToTicketAsync(
        Guid ticketId,
        Guid articleId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<KbOutcome> UnlinkFromTicketAsync(
        Guid ticketId,
        Guid articleId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes an article nothing points at. Refused once a ticket has been answered with it — archiving is
    /// how an article goes out of use, following WP-5.6's rule for a runbook that has run.
    /// </summary>
    Task<KbOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);
}
