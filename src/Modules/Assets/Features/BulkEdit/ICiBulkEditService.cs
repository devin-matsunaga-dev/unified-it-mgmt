using System.Security.Claims;

namespace Modules.Assets.Features.BulkEdit;

public interface ICiBulkEditService
{
    Task<BulkEditReport> ApplyAsync(
        BulkEditCisRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
