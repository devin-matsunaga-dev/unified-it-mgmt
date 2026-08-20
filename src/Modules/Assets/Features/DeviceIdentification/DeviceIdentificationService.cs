using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Assets.Data;
using Platform.Auditing;

namespace Modules.Assets.Features.DeviceIdentification;

public interface IDeviceIdentificationService
{
    Task<IdentifyDeviceResponse> IdentifyAsync(
        IdentifyDeviceRequest request, CancellationToken cancellationToken);

    Task<ProductCatalogResult> SaveEntryAsync(
        SaveProductCatalogEntryRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductCatalogEntryResponse>> ListEntriesAsync(
        string? search, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a handful of scanned strings into a claim about what the device is — or, far more often at
/// first, into an honest "not known" with the scans preserved.
/// <para>
/// The service owns the order — parse, then ask each provider in turn — and nothing else. Parsing is
/// pure and lives in <see cref="BarcodeParser"/>; every lookup is behind
/// <see cref="IDeviceLookupProvider"/>, including the local catalogue, so a manufacturer integration
/// is an added registration rather than an edit here.
/// </para>
/// </summary>
public sealed class DeviceIdentificationService(
    AssetsDbContext dbContext,
    IEnumerable<IDeviceLookupProvider> providers,
    IAuditService auditService,
    ILogger<DeviceIdentificationService> logger) : IDeviceIdentificationService
{
    /// <summary>
    /// A ceiling on one identification. A device wears a handful of barcodes; a request carrying
    /// hundreds is either a mistake or an attempt to make the server do unbounded work.
    /// </summary>
    public const int MaxScans = 12;

    public async Task<IdentifyDeviceResponse> IdentifyAsync(
        IdentifyDeviceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identifiers = new List<IdentifierView>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scan in request.Scans.Take(MaxScans))
        {
            var parsed = BarcodeParser.Parse(scan);
            if (parsed is null)
            {
                // Kept and reported rather than dropped: a technician who scanned something needs to
                // know it was refused, or they will scan it again and again.
                rejected.Add(Truncate(scan));
                continue;
            }

            Add(parsed);
            if (parsed.AlsoCarried is { } second) Add(second);
        }

        var result = identifiers.Count == 0
            ? DeviceIdentificationResult.None
            : await AskProvidersAsync(identifiers, cancellationToken);

        // The serial belongs to the device in the technician's hand, never to whatever a provider
        // matched — a catalogue entry describes a product and has no serial to give.
        var serial = identifiers.FirstOrDefault(item => item.Kind == IdentifierKind.SerialNumber)?.Value;
        if (serial is not null) result = result with { SerialNumber = serial };

        return new IdentifyDeviceResponse(identifiers, rejected, result);

        void Add(ParsedIdentifier parsed)
        {
            // A duplicate scan is the normal case — a technician sweeping a label twice — so it is
            // silently the same identifier rather than an error or a second row.
            if (!seen.Add($"{parsed.Kind}:{parsed.Value}")) return;
            identifiers.Add(new IdentifierView(parsed.RawValue, parsed.Value, parsed.Kind));
        }
    }

    private async Task<DeviceIdentificationResult> AskProvidersAsync(
        IReadOnlyList<IdentifierView> identifiers,
        CancellationToken cancellationToken)
    {
        foreach (var provider in providers.OrderBy(item => item.Order))
        {
            try
            {
                var result = await provider.LookupAsync(identifiers, cancellationToken);
                if (result.Confidence is not IdentificationConfidence.Unknown) return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A provider is a convenience. One that fails must not stop the others being asked,
                // and must never stop a device being registered by hand.
                logger.LogWarning(exception, "Device lookup provider {Provider} failed.", provider.Name);
            }
        }

        return DeviceIdentificationResult.None;
    }

    public async Task<ProductCatalogResult> SaveEntryAsync(
        SaveProductCatalogEntryRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parsed = BarcodeParser.Parse(request.ModelIdentifier);
        if (parsed is null)
        {
            return new(ProductCatalogOutcome.Duplicate, Error: "That is not a usable model identifier.");
        }

        var key = parsed.Value;
        var now = DateTimeOffset.UtcNow;
        var existing = await dbContext.ProductCatalogEntries
            .SingleOrDefaultAsync(entry => entry.ModelIdentifier == key, cancellationToken);

        var before = existing is null ? null : Map(existing);
        var entry = existing ?? new ProductCatalogEntry
        {
            Id = Guid.CreateVersion7(),
            ModelIdentifier = key,
            Manufacturer = string.Empty,
            Model = string.Empty,
            // Typed by a person until a manufacturer provider overwrites it, and recorded as such so
            // a prefill can say where it came from.
            Source = ProductCatalogSource.Manual,
            CreatedBy = GetActorId(actor),
            CreatedAt = now,
        };

        entry.Manufacturer = request.Manufacturer.Trim();
        entry.Model = request.Model.Trim();
        entry.ProductNumber = Normalise(request.ProductNumber);
        entry.DeviceType = Normalise(request.DeviceType);
        entry.UpdatedAt = now;

        if (existing is null) dbContext.ProductCatalogEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(entry);
        await auditService.WriteAsync(
            actor, existing is null ? "Created" : "Updated", "ProductCatalogEntry",
            entry.Id.ToString(), before, response, cancellationToken);
        return new(ProductCatalogOutcome.Success, response);
    }

    public async Task<IReadOnlyList<ProductCatalogEntryResponse>> ListEntriesAsync(
        string? search,
        CancellationToken cancellationToken)
    {
        var query = dbContext.ProductCatalogEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(entry =>
                EF.Functions.ILike(entry.ModelIdentifier, term)
                || EF.Functions.ILike(entry.Manufacturer, term)
                || EF.Functions.ILike(entry.Model, term));
        }

        return await query
            .OrderBy(entry => entry.Manufacturer).ThenBy(entry => entry.Model)
            .Select(entry => Map(entry))
            .ToListAsync(cancellationToken);
    }

    internal static ProductCatalogEntryResponse Map(ProductCatalogEntry entry) => new(
        entry.Id, entry.ModelIdentifier, entry.Manufacturer, entry.Model, entry.ProductNumber,
        entry.DeviceType, entry.Source, entry.CreatedBy, entry.CreatedAt, entry.UpdatedAt);

    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value) =>
        value.Length <= 40 ? value : value[..40] + "…";

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("preferred_username") ?? actor.FindFirstValue("sub") ?? "unknown";
}
