using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Modules.Assets.Data;

namespace Modules.Assets.Features.Import;

public interface ICiImportService
{
    /// <summary>Reads the uploaded file's header row and proposes which column feeds which CI field.</summary>
    Task<CiImportColumnsResult> InspectAsync(IFormFile file, CiType type, CancellationToken cancellationToken);

    /// <summary>Classifies every row without writing anything. The commit repeats exactly this work.</summary>
    Task<CiImportResult> PreviewAsync(IFormFile file, CiImportMapping mapping, CancellationToken cancellationToken);

    Task<CiImportResult> CommitAsync(
        IFormFile file,
        CiImportMapping mapping,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
