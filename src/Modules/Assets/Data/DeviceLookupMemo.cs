namespace Modules.Assets.Data;

/// <summary>
/// What an external provider once said about one specific device, so it is never asked twice.
/// <para>
/// **This is a cache, not a catalogue, and the difference is the whole reason it is a separate
/// table.** <see cref="ProductCatalogEntry"/> is keyed on a product identifier and answers for every
/// device of that model. A memo is keyed on the exact identifier that was looked up — a Dell service
/// tag names one machine — and answers only for that machine. It must never be consulted for a
/// different identifier, or a serial would become a product key and hand one device's model to
/// everything that follows.
/// </para>
/// <para>
/// It exists because Dell's API is keyed by service tag. A product catalogue cannot short-circuit a
/// device nobody has scanned before, so without this a delivery of twenty identical laptops is
/// twenty API calls; with it, re-scanning any of them is none.
/// </para>
/// </summary>
public sealed class DeviceLookupMemo
{
    public Guid Id { get; set; }

    /// <summary>The identifier that was looked up, normalised. Unique — one device, one answer.</summary>
    public required string Identifier { get; set; }

    public string? Manufacturer { get; set; }

    public string? Model { get; set; }

    public string? ProductNumber { get; set; }

    public string? DeviceType { get; set; }

    /// <summary>Who answered — kept so a cached answer can still say where it came from.</summary>
    public required string Source { get; set; }

    /// <summary>
    /// When the answer was fetched. A model does not change under a device, so nothing expires this
    /// today; the column exists so a future decision to re-fetch has something to decide against.
    /// </summary>
    public DateTimeOffset FetchedAt { get; set; }
}
