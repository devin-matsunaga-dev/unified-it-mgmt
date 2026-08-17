using System.Security.Claims;

namespace Modules.Helpdesk.Features.Problems;

/// <summary>
/// Problems and the known errors among them.
/// <para>
/// Agent-only throughout. <c>CanManageTickets</c> includes EndUser so that requesters can reach the
/// portal (WP-0.3), so the policy at the door is not enough on its own and every method here refuses a
/// requester the way <c>TicketViewService</c> does — a problem names causes, workarounds and other
/// people's incidents, and none of that is a requester's to read.
/// </para>
/// </summary>
public interface IProblemService
{
    Task<ProblemPageResponse> ListAsync(
        ProblemListFilter filter,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>One problem with its incidents attached. Null when it does not exist or the actor may not see it.</summary>
    Task<ProblemResponse?> GetAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<ProblemResult> CreateAsync(
        CreateProblemRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ProblemResult> UpdateAsync(
        Guid id,
        UpdateProblemRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a problem through <see cref="ProblemWorkflow"/>. Closing one answers with the knowledge
    /// article draft the WP asks it to prompt for.
    /// </summary>
    Task<ProblemTransitionResult> TransitionAsync(
        Guid id,
        ProblemTransitionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ProblemResult> LinkIncidentAsync(
        Guid id,
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ProblemOutcome> UnlinkIncidentAsync(
        Guid id,
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// The draft on demand, at any point in a problem's life. The close prompt is the moment it matters,
    /// but a draft that can only be seen once is one somebody has to get right first time.
    /// </summary>
    Task<KnowledgeDraftResponse?> GetKnowledgeDraftAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// The problems an incident belongs to, for the panel on the ticket screen. Empty for most tickets.
    /// <para>
    /// A read of Helpdesk's own tables from Helpdesk's own service, so no port is involved: the ticket and
    /// the problem are both in this schema, which is the whole reason the link could be a foreign key.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ProblemResponse>> ListForTicketAsync(
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
