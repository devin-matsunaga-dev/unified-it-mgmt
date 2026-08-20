using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;

namespace Modules.Assets.Features.DeviceIdentification;

/// <summary>
/// Answers from what an external provider already said about this exact device, so it is never asked
/// twice. Runs after the product catalogue and before anything that leaves the building.
/// <para>
/// It matches the identifier verbatim and generalises nothing. A memo about service tag 7XKLM92
/// answers for 7XKLM92 and for no other string — which is what keeps a per-device cache from
/// quietly becoming a product mapping.
/// </para>
/// <para>
/// Confidence is <see cref="IdentificationConfidence.Medium"/> rather than High even when the
/// original answer was authoritative: this is a remembered answer about one machine, not a match
/// against a product record, and the screen should invite a glance.
/// </para>
/// </summary>
public sealed class CachedLookupProvider(AssetsDbContext dbContext) : IDeviceLookupProvider
{
    public int Order => 5;

    public string Name => "Remembered lookup";

    public async Task<DeviceIdentificationResult> LookupAsync(
        IReadOnlyList<IdentifierView> identifiers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        var keys = identifiers.Select(identifier => identifier.Value)
            .Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return DeviceIdentificationResult.None;

        var memo = await dbContext.DeviceLookupMemos.AsNoTracking()
            .FirstOrDefaultAsync(entry => keys.Contains(entry.Identifier), cancellationToken);
        if (memo is null || memo.Model is null) return DeviceIdentificationResult.None;

        return new DeviceIdentificationResult
        {
            Manufacturer = memo.Manufacturer,
            Model = memo.Model,
            ProductNumber = memo.ProductNumber,
            DeviceType = memo.DeviceType,
            Source = memo.Source,
            Confidence = IdentificationConfidence.Medium,
        };
    }
}
