using Modules.Assets.Features.Cis;

namespace Modules.Assets.Features.Labels;

/// <summary>Printable asset labels, and the reverse trip from a scanned code back to its CI.</summary>
public interface ICiLabelService
{
    Task<CiLabelResult> RenderAsync(
        IReadOnlyList<Guid> ciIds,
        CiLabelSize size,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves whatever a scanner produced — a label URL, a bare id, a serial number, an asset tag
    /// — to the CI it names, or null when nothing matches.
    /// </summary>
    Task<CiResponse?> LookupAsync(string code, CancellationToken cancellationToken);
}
