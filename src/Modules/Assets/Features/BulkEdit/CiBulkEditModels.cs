using Modules.Assets.Data;

namespace Modules.Assets.Features.BulkEdit;

/// <summary>
/// Ownership is a complete statement, exactly as <c>PUT /api/cis/{id}/assignment</c> is: sending it
/// with a null owner checks every selected CI in. Omitting the whole object leaves ownership alone.
/// </summary>
public sealed record CiOwnershipChange(Guid? OwnerUserId, Guid? DepartmentId, Guid? SiteId);

public sealed record BulkEditCisRequest(
    IReadOnlyList<Guid> CiIds,
    CiOwnershipChange? Ownership = null,
    CiLifecycleState? LifecycleState = null,
    string? Note = null);

public sealed record BulkEditRowResult(Guid CiId, string? Name, bool Succeeded, string? Error);

public sealed record BulkEditReport(
    int Total,
    int Succeeded,
    int Failed,
    IReadOnlyList<BulkEditRowResult> Rows);
