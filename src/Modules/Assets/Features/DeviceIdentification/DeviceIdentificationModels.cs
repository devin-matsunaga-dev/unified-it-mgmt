using Modules.Assets.Data;

namespace Modules.Assets.Features.DeviceIdentification;

/// <summary>
/// How much of the answer is actually known. The distinction that matters operationally is between
/// <see cref="High"/> — an exact product identifier matched an authoritative record — and everything
/// below it, because only the first is safe to accept without a person reading it.
/// </summary>
public enum IdentificationConfidence
{
    /// <summary>Nothing resolved. The scans are preserved and the technician fills the form.</summary>
    Unknown,

    /// <summary>A partial or ambiguous match. Shown, never applied on its own.</summary>
    Low,

    /// <summary>A provider answered but some scanned identifiers could not be corroborated.</summary>
    Medium,

    /// <summary>An exact product identifier matched an authoritative record.</summary>
    High,
}

/// <param name="Scanned">Exactly what the scanner emitted, preserved for audit.</param>
public sealed record IdentifierView(string Scanned, string Value, IdentifierKind Kind);

/// <summary>
/// What a lookup knows about a device. Every field is optional because a provider that knows the
/// manufacturer and nothing else is still worth more than nothing, and the caller decides what to do
/// with a partial answer.
/// </summary>
public sealed record DeviceIdentificationResult
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? ProductNumber { get; init; }
    public string? SerialNumber { get; init; }
    public string? DeviceType { get; init; }

    /// <summary>Where the answer came from — a catalogue entry's source, or "Manual", or "Unknown".</summary>
    public string Source { get; init; } = "Unknown";

    public IdentificationConfidence Confidence { get; init; } = IdentificationConfidence.Unknown;

    public static DeviceIdentificationResult None { get; } = new();
}

/// <summary>
/// One place a device can be looked up. Implementations are ordered by the identification service and
/// asked in turn; the local catalogue is simply the first provider rather than a special case.
/// <para>
/// A provider must never throw for a device it cannot identify, and must never let a third party's
/// outage reach the caller — an unidentified device still has to be registerable by hand.
/// </para>
/// </summary>
public interface IDeviceLookupProvider
{
    /// <summary>Ordered ascending; the local catalogue runs first because it costs nothing.</summary>
    int Order { get; }

    string Name { get; }

    /// <summary>
    /// Returns what this provider knows, or <see cref="DeviceIdentificationResult.None"/>. Never
    /// throws for an unknown device and never surfaces a transport failure.
    /// </summary>
    Task<DeviceIdentificationResult> LookupAsync(
        IReadOnlyList<IdentifierView> identifiers,
        CancellationToken cancellationToken);
}

public sealed record IdentifyDeviceRequest(IReadOnlyList<string> Scans);

/// <param name="Identifiers">Every scan, classified, in the order they arrived.</param>
/// <param name="Rejected">Scans that could not be used at all — too long, or unprintable.</param>
public sealed record IdentifyDeviceResponse(
    IReadOnlyList<IdentifierView> Identifiers,
    IReadOnlyList<string> Rejected,
    DeviceIdentificationResult Result);

public sealed record SaveProductCatalogEntryRequest(
    string ModelIdentifier,
    string Manufacturer,
    string Model,
    string? ProductNumber = null,
    string? DeviceType = null);

public sealed record ProductCatalogEntryResponse(
    Guid Id,
    string ModelIdentifier,
    string Manufacturer,
    string Model,
    string? ProductNumber,
    string? DeviceType,
    ProductCatalogSource Source,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum ProductCatalogOutcome
{
    Success,
    NotFound,
    Duplicate,
}

public sealed record ProductCatalogResult(
    ProductCatalogOutcome Outcome,
    ProductCatalogEntryResponse? Entry = null,
    string? Error = null);
