using System.Security.Claims;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Cis;

/// <summary>
/// The Assets module's public surface for configuration items. Other modules read CIs through this
/// interface — they never query the <c>assets</c> schema directly.
/// </summary>
public interface ICiService
{
    Task<CiPageResponse> ListAsync(CiListRequest request, CancellationToken cancellationToken);

    Task<CiResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<CiResult> CreateAsync(CreateCiRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<CiResult> UpdateAsync(Guid id, UpdateCiRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<CiOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<CiTypeSchemaResponse>> GetSchemasAsync(CancellationToken cancellationToken);

    Task<CiCustomFieldResult> AddFieldAsync(
        CreateCiCustomFieldRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CiOutcome> DeleteFieldAsync(Guid fieldId, ClaimsPrincipal actor, CancellationToken cancellationToken);
}
