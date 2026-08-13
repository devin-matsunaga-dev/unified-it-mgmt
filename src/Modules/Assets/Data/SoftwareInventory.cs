namespace Modules.Assets.Data;

/// <summary>
/// A canonical product in the normalisation catalogue: what a raw installed-software string is
/// understood to be, and the thing a licence pool entitles.
/// <para>
/// This is deliberately not a <see cref="SoftwareCi"/>. A software CI is a managed item of the estate
/// with a lifecycle, an owner and dependency edges — "the payroll application". A product is a
/// catalogue row that thousands of installs point at, has no lifecycle and is never related to
/// anything. Merging them would put a lifecycle state on "Google Chrome" and a check-in log on a
/// licence.
/// </para>
/// </summary>
public sealed class SoftwareProduct
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Notes { get; set; }

    /// <summary>Deactivation is the way out of a product nothing should be deleted: see the delete guard.</summary>
    public bool IsActive { get; set; } = true;

    public ICollection<SoftwareNormalisationRule> Rules { get; set; } = [];
    public ICollection<LicensePool> LicensePools { get; set; } = [];
    public ICollection<InstalledSoftware> Installs { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>How a rule's pattern is compared against a raw software name, most specific first.</summary>
public enum SoftwareMatchKind
{
    Exact = 1,
    Prefix = 2,
    Contains = 3,
}

/// <summary>
/// One line of the normalisation catalogue: a pattern over the raw name a machine reported, and the
/// product it means. Rules are data an operator reads and extends rather than a compiled-in table,
/// which is what makes "add a rule, re-normalise, the history follows" possible.
/// </summary>
public sealed class SoftwareNormalisationRule
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public SoftwareProduct Product { get; set; } = null!;
    public SoftwareMatchKind MatchKind { get; set; }

    /// <summary>Stored already canonicalised (trimmed, whitespace-collapsed, lower-cased).</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Lower wins. Only ever consulted within one match kind — a Prefix never beats an Exact.</summary>
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One piece of software seen on one CI. The raw fields are what the machine reported, kept verbatim;
/// <see cref="ProductId"/> is what the catalogue made of it and is null until a rule matches.
/// <para>
/// The version is never parsed into the product. No two publishers spell a version the same way, and a
/// regex that guesses one is confidently wrong rather than plainly raw — the same call WP-4.2 made for
/// <c>sysDescription</c>.
/// </para>
/// </summary>
public sealed class InstalledSoftware
{
    public Guid Id { get; set; }
    public Guid CiId { get; set; }
    public ConfigurationItem Ci { get; set; } = null!;

    /// <summary>
    /// The dedupe key within a CI: the canonical raw name and version joined. A composite unique index
    /// over the two nullable columns would treat two unknown versions as distinct rows in Postgres, so
    /// the key is materialised instead — the same shape as the discovery ledger's identity key.
    /// </summary>
    public string IdentityKey { get; set; } = string.Empty;

    public string RawName { get; set; } = string.Empty;
    public string? RawPublisher { get; set; }
    public string? Version { get; set; }
    public Guid? ProductId { get; set; }
    public SoftwareProduct? Product { get; set; }
    public DateOnly? InstalledOn { get; set; }

    /// <summary>Where this row came from — the imported file's name — so a bad import can be traced back.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int SightingCount { get; set; }
}

/// <summary>
/// A block of entitlements for one product. A product can have several — licences are bought over
/// time — so compliance is computed per product by summing the pools that are active and unexpired.
/// </summary>
public sealed class LicensePool
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public SoftwareProduct Product { get; set; } = null!;
    public string Name { get; set; } = string.Empty;

    /// <summary>The agreement or purchase order this block was bought under, as the operator types it.</summary>
    public string? Reference { get; set; }

    public int Entitlements { get; set; }
    public DateOnly? PurchaseDate { get; set; }

    /// <summary>Null means perpetual: a licence with no end date is never expiring and never notifies.</summary>
    public DateOnly? ExpiresAt { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
