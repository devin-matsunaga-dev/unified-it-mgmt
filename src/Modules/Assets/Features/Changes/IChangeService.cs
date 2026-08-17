using System.Security.Claims;

namespace Modules.Assets.Features.Changes;

public interface IChangeService
{
    Task<ChangePageResponse> ListAsync(ChangeListRequest request, CancellationToken cancellationToken);

    Task<ChangeResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ChangeResult> CreateAsync(
        CreateChangeRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ChangeResult> UpdateAsync(
        Guid id,
        UpdateChangeRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves the change through <see cref="ChangeWorkflow"/>. Approving is the one transition with a
    /// consequence outside this module: it publishes <c>ChangeRequestApproved</c> through the outbox.
    /// </summary>
    Task<ChangeResult> TransitionAsync(
        Guid id,
        ChangeTransitionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
