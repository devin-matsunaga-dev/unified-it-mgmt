using System.Security.Claims;

namespace Modules.Assets.Features.Lifecycle;

/// <summary>
/// Lifecycle state and ownership for configuration items. Both mutate the same CI row and share the
/// rule that a disposed CI is frozen, so they live behind one service.
/// </summary>
public interface ICiLifecycleService
{
    Task<IReadOnlyList<CiLifecycleStateResponse>> GetStatesAsync(CancellationToken cancellationToken);

    Task<CiLifecycleResult> TransitionAsync(
        Guid ciId,
        TransitionCiRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CiLifecycleHistoryResponse>?> GetHistoryAsync(Guid ciId, CancellationToken cancellationToken);

    Task<CiLifecycleResult> AssignAsync(
        Guid ciId,
        AssignCiRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CiAssignmentResponse>?> GetAssignmentsAsync(Guid ciId, CancellationToken cancellationToken);
}
