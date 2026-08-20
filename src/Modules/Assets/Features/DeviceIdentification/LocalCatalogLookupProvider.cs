using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;

namespace Modules.Assets.Features.DeviceIdentification;

/// <summary>
/// The first provider asked, and the only one that exists in Phase 1. It answers from
/// <see cref="ProductCatalogEntry"/>, which costs a single indexed read and no third party.
/// <para>
/// It looks up <see cref="IdentifierKind.ModelIdentifier"/> values only. A serial number identifies
/// one machine, so matching on one would mean the catalogue had been taught that a specific device's
/// serial implies a model — and the next unrelated device sharing that string would inherit it. That
/// refusal is the safety property this whole feature rests on, so it is enforced here rather than
/// left to callers to remember.
/// </para>
/// </summary>
public sealed class LocalCatalogLookupProvider(AssetsDbContext dbContext) : IDeviceLookupProvider
{
    public int Order => 0;

    public string Name => "Local catalogue";

    public async Task<DeviceIdentificationResult> LookupAsync(
        IReadOnlyList<IdentifierView> identifiers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        var keys = identifiers
            .Where(identifier => identifier.Kind == IdentifierKind.ModelIdentifier)
            .Select(identifier => identifier.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0) return DeviceIdentificationResult.None;

        var matches = await dbContext.ProductCatalogEntries.AsNoTracking()
            .Where(entry => keys.Contains(entry.ModelIdentifier))
            .ToListAsync(cancellationToken);
        if (matches.Count == 0) return DeviceIdentificationResult.None;

        var entry = matches[0];
        return new DeviceIdentificationResult
        {
            Manufacturer = entry.Manufacturer,
            Model = entry.Model,
            ProductNumber = entry.ProductNumber ?? entry.ModelIdentifier,
            DeviceType = entry.DeviceType,
            Source = entry.Source.ToString(),
            // Two catalogue entries matched by different scanned identifiers disagree about what this
            // device is. Both are exact matches, so neither is wrong — which is precisely a case for a
            // person to settle rather than for the server to pick a winner.
            Confidence = matches.Count == 1
                ? IdentificationConfidence.High
                : IdentificationConfidence.Low,
        };
    }
}
