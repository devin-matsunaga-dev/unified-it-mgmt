namespace Modules.Assets.Data;

/// <summary>
/// One product, keyed by an identifier the manufacturer prints on every device of that model — an HP
/// product number, a Lenovo MTM, a Cisco PID, an SKU.
/// <para>
/// The catalogue holds <em>products</em> and never individual devices. A serial number identifies one
/// machine and is worthless as a reuse key: caching a serial-to-model mapping would mean the next
/// scan of an unrelated device inherits whatever the first one happened to be. That separation is the
/// whole point of this table, and it is enforced by what the identification service is willing to
/// write here rather than by the schema alone.
/// </para>
/// </summary>
public sealed class ProductCatalogEntry
{
    public Guid Id { get; set; }

    /// <summary>
    /// The printed identifier, upper-cased and trimmed. Unique: the reuse key has to resolve to one
    /// product or the catalogue answers a question with two answers.
    /// </summary>
    public required string ModelIdentifier { get; set; }

    public required string Manufacturer { get; set; }

    /// <summary>What a person calls it — "ThinkPad X1 Carbon Gen 11", not "21HM0001US".</summary>
    public required string Model { get; set; }

    /// <summary>The manufacturer's own product/part number, when it differs from the key scanned.</summary>
    public string? ProductNumber { get; set; }

    /// <summary>Laptop, Desktop, Switch — free text, because the vocabulary is the vendor's.</summary>
    public string? DeviceType { get; set; }

    /// <summary>
    /// Where this came from, kept even after caching. A mapping typed by a technician and one returned
    /// by a manufacturer's API are both usable and are not equally trustworthy, and the screen that
    /// shows a prefill has to be able to say which it is.
    /// </summary>
    public required ProductCatalogSource Source { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ProductCatalogSource
{
    /// <summary>Typed by a person. Usable, unverified, and propagates a typo to every later device.</summary>
    Manual,

    /// <summary>Returned by a manufacturer's own API and cached here.</summary>
    Dell,
    Lenovo,
    Hp,
    Cisco,
}
