using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

namespace Modules.Assets.Features.Labels;

public sealed class CiLabelService(
    AssetsDbContext dbContext,
    ICiService ciService,
    IConfiguration configuration) : ICiLabelService
{
    /// <summary>The list page's ceiling, so a "select all and print" cannot ask for more than it shows.</summary>
    internal const int MaximumSheetSize = 200;

    public async Task<CiLabelResult> RenderAsync(
        IReadOnlyList<Guid> ciIds,
        CiLabelSize size,
        CancellationToken cancellationToken)
    {
        if (ciIds.Count == 0)
        {
            return Invalid("Select at least one configuration item to print.");
        }

        if (ciIds.Count > MaximumSheetSize)
        {
            return Invalid($"A label sheet holds at most {MaximumSheetSize} labels; {ciIds.Count} were requested.");
        }

        var distinct = ciIds.Distinct().ToList();
        var found = await dbContext.Cis
            .Where(ci => distinct.Contains(ci.Id))
            .Select(ci => new { ci.Id, ci.Name, ci.AssetTag, ci.SerialNumber, ci.Type })
            .ToDictionaryAsync(ci => ci.Id, cancellationToken);

        // An unknown id means the selection has gone stale, and a sheet that silently prints 19
        // labels for 20 selected assets is one nobody notices is short until the labels are stuck on.
        if (distinct.Cast<Guid?>().FirstOrDefault(id => !found.ContainsKey(id!.Value)) is { } missing)
        {
            return new(CiLabelOutcome.NotFound, Error: $"CI '{missing}' does not exist.");
        }

        // Blank counts as unset, so emptying the override falls back to the CORS origin rather than
        // silently printing labels that point at the host's own loopback.
        var baseUrl = Configured(CiLabelCodes.PublicBaseUrlKey) ?? Configured(CiLabelCodes.WebClientOriginKey);
        var labels = distinct
            .Select(id => found[id])
            .Select(ci => new CiLabel(
                ci.Id, ci.Name, ci.AssetTag, ci.SerialNumber, ci.Type, CiLabelCodes.PayloadFor(baseUrl, ci.Id)))
            .ToList();

        return new(CiLabelOutcome.Success, CiLabelDocument.Render(labels, size), FileNameFor(labels));
    }

    public async Task<CiResponse?> LookupAsync(string code, CancellationToken cancellationToken)
    {
        var id = await ResolveAsync(code, cancellationToken);
        return id is null ? null : await ciService.GetAsync(id.Value, cancellationToken);
    }

    private async Task<Guid?> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        var trimmed = code.Trim();
        if (CiLabelCodes.TryReadCiId(trimmed, out var scanned))
        {
            return await dbContext.Cis.AnyAsync(ci => ci.Id == scanned, cancellationToken) ? scanned : null;
        }

        // Serial first, then asset tag: the same order and the same case-insensitivity WP-2.5 gave
        // import dedupe, so a scanner and an import agree on which CI a code names.
        var bySerial = await dbContext.Cis
            .Where(ci => ci.SerialNumber != null && ci.SerialNumber.ToLower() == trimmed.ToLower())
            .Select(ci => (Guid?)ci.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (bySerial is not null)
        {
            return bySerial;
        }

        return await dbContext.Cis
            .Where(ci => ci.AssetTag != null && ci.AssetTag.ToLower() == trimmed.ToLower())
            .Select(ci => (Guid?)ci.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private string? Configured(string key) =>
        configuration[key] is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string FileNameFor(IReadOnlyList<CiLabel> labels) => labels.Count == 1
        ? $"asset-label-{Slug(labels[0].AssetTag ?? labels[0].CiId.ToString())}.pdf"
        : $"asset-labels-{labels.Count}.pdf";

    private static string Slug(string value) =>
        new([.. value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')]);

    private static CiLabelResult Invalid(string message) => new(
        CiLabelOutcome.Invalid,
        Errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["ciIds"] = [message] });
}
