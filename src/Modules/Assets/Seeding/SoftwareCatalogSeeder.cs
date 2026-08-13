using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Modules.Assets.Features.Software;

namespace Modules.Assets.Seeding;

public sealed record SoftwareCatalogSeedResult(
    int ProductsAdded,
    int RulesAdded,
    int InstallsAdded,
    int LicensePoolsAdded);

/// <summary>A catalogue product plus the one rule that reaches it. Keyed by a slug the rest of the seed names.</summary>
public sealed record SoftwareProductSeed(
    string Key,
    string Publisher,
    string Name,
    string Category,
    SoftwareMatchKind MatchKind,
    string Pattern);

/// <summary>What one machine reported, spelled the way a real inventory agent spells it.</summary>
public sealed record InstalledSoftwareSeed(string CiKey, string RawName, string Publisher, string Version);

/// <summary>
/// A block of entitlements. <paramref name="ExpiresInDays"/> is an offset from the day the seeder runs,
/// following the WP-2.8 rule: the dev database is recreated on most AppHost restarts and a fixed date
/// would drift into the past. Null is a perpetual licence.
/// </summary>
public sealed record LicensePoolSeed(
    string ProductKey,
    string Name,
    string Reference,
    int Entitlements,
    int? ExpiresInDays,
    string? Notes = null);

/// <summary>
/// Seeds the software inventory the WP's verification steps need: a catalogue of nine products, five
/// laptops' worth of installs with the raw names an agent actually reports, and licence pools placed so
/// that every compliance state is on the screen after a fresh <c>aspire run</c> — including the one the
/// WP names, a pool of three against five installs.
/// <para>
/// Written through the DbContext rather than the services, following the WP-2.8 estate seeder: the
/// import path is one audit entry per file and the catalogue services are one per product, which is
/// right for an operator's work and wrong for reference data nobody performed. Re-running adds nothing —
/// every id is derived from its position in the tables below.
/// </para>
/// </summary>
public sealed class SoftwareCatalogSeeder(AssetsDbContext dbContext)
{
    /// <summary>The file name recorded against a seeded install, so its origin is legible on the CI page.</summary>
    public const string SeedSource = "seeded-inventory.csv";

    private const int ProductKind = 9;
    private const int RuleKind = 10;
    private const int InstallKind = 11;
    private const int PoolKind = 12;

    /// <summary>
    /// The catalogue. Every rule is a prefix: an inventory agent reports "Google Chrome 121.0.6167.140"
    /// and the product is the part in front of the version, which is exactly what a prefix says. Nothing
    /// here parses the version — that stays verbatim on the install row.
    /// </summary>
    public static readonly IReadOnlyList<SoftwareProductSeed> Products =
    [
        new("windows", "Microsoft", "Windows 11 Pro", "Operating system", SoftwareMatchKind.Prefix, "microsoft windows 11 pro"),
        new("office", "Microsoft", "Office Professional Plus", "Productivity", SoftwareMatchKind.Prefix, "microsoft office professional plus"),
        new("teams", "Microsoft", "Teams", "Collaboration", SoftwareMatchKind.Prefix, "microsoft teams"),
        new("chrome", "Google", "Chrome", "Browser", SoftwareMatchKind.Prefix, "google chrome"),
        new("firefox", "Mozilla", "Firefox", "Browser", SoftwareMatchKind.Prefix, "mozilla firefox"),
        new("acrobat", "Adobe", "Acrobat Pro", "Productivity", SoftwareMatchKind.Prefix, "adobe acrobat pro"),
        new("sevenzip", "Igor Pavlov", "7-Zip", "Utility", SoftwareMatchKind.Prefix, "7-zip"),
        new("notepadpp", "Notepad++ Team", "Notepad++", "Developer tool", SoftwareMatchKind.Prefix, "notepad++"),
        new("zoom", "Zoom", "Zoom Workplace", "Collaboration", SoftwareMatchKind.Prefix, "zoom workplace"),
    ];

    /// <summary>
    /// Five laptops' inventory. Adobe Acrobat Pro is on all five against a pool of three, which is the
    /// WP's own verification case standing up on a fresh database. "Contoso VPN Client" is on two of
    /// them and matches no rule on purpose: the unrecognised list has to have something in it for
    /// "add a rule, re-normalise, watch the history follow" to be demonstrable.
    /// </summary>
    public static readonly IReadOnlyList<InstalledSoftwareSeed> Installs =
    [
        .. new[] { "hw-lt-01", "hw-lt-02", "hw-lt-03", "hw-lt-04", "hw-lt-05" }.SelectMany(ci => new[]
        {
            new InstalledSoftwareSeed(ci, "Microsoft Windows 11 Pro", "Microsoft Corporation", "10.0.26100"),
            new InstalledSoftwareSeed(ci, "Google Chrome", "Google LLC", "121.0.6167.140"),
            new InstalledSoftwareSeed(ci, "Microsoft Teams (work or school)", "Microsoft Corporation", "24004.1309"),
            new InstalledSoftwareSeed(ci, "7-Zip 23.01 (x64)", "Igor Pavlov", "23.01"),
            new InstalledSoftwareSeed(ci, "Adobe Acrobat Pro (64-bit)", "Adobe Inc.", "24.001.20604"),
        }),
        new("hw-lt-01", "Microsoft Office Professional Plus 2021 - en-us", "Microsoft Corporation", "16.0.14332.20481"),
        new("hw-lt-02", "Microsoft Office Professional Plus 2021 - en-us", "Microsoft Corporation", "16.0.14332.20481"),
        new("hw-lt-03", "Microsoft Office Professional Plus 2021 - en-us", "Microsoft Corporation", "16.0.14332.20481"),
        new("hw-lt-04", "Mozilla Firefox (x64 en-GB)", "Mozilla", "123.0.1"),
        new("hw-lt-05", "Mozilla Firefox (x64 en-GB)", "Mozilla", "123.0.1"),
        new("hw-lt-02", "Contoso VPN Client", "Contoso Networks", "4.2.7"),
        new("hw-lt-05", "Contoso VPN Client", "Contoso Networks", "4.2.7"),
        new("hw-lt-01", "Notepad++ (64-bit x64)", "Notepad++ Team", "8.6.4"),
    ];

    /// <summary>
    /// Placed so a fresh run shows all four compliance states at once: Acrobat over-deployed, Office and
    /// Windows compliant, Zoom bought and used by nobody, and Chrome installed everywhere under no
    /// licence at all. The Teams pool lapses inside the 7-day window so the renewal pass has something
    /// true to raise on its first sweep.
    /// </summary>
    public static readonly IReadOnlyList<LicensePoolSeed> LicensePools =
    [
        new("acrobat", "Acrobat Pro volume subscription", "PO-2025-0410", 3, 300,
            "Bought for the three people who edit PDFs; IT has since installed it on every laptop."),
        new("office", "Office LTSC volume licence", "PO-2024-0141", 5, null),
        new("windows", "Windows 11 Pro OEM entitlements", "PO-2024-0141", 25, null),
        new("teams", "Teams Enterprise subscription", "SUB-88213", 20, 5,
            "Renews annually; this is the seat block that lapses first."),
        new("zoom", "Zoom Workplace Pro seats", "SUB-90114", 10, 180,
            "Bought for the meeting rooms and never rolled out."),
    ];

    public async Task<SoftwareCatalogSeedResult> SeedAsync(
        IReadOnlyDictionary<string, Guid> ciIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciIds);
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var productIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var productsAdded = 0;
        var rulesAdded = 0;

        // Guarded on the natural keys rather than on the deterministic ids alone. A product's
        // (publisher, name) and a rule's (kind, pattern) each carry a unique index, so a row somebody
        // typed by hand — or a test that got there first — would otherwise make this seeder crash on a
        // database it is supposed to be able to run against. Where one exists it is adopted, so the
        // rules and pools below hang off the row that is really there.
        var seededNames = Products.Select(product => product.Name).ToArray();
        var existingProducts = await dbContext.SoftwareProducts
            .Where(product => seededNames.Contains(product.Name))
            .Select(product => new { product.Id, product.Name, product.Publisher })
            .ToListAsync(cancellationToken);
        var seededPatterns = Products.Select(product => SoftwareNormaliser.Canonicalise(product.Pattern)).ToArray();
        var existingRules = await dbContext.SoftwareNormalisationRules
            .Where(rule => seededPatterns.Contains(rule.Pattern))
            .Select(rule => new { rule.MatchKind, rule.Pattern })
            .ToListAsync(cancellationToken);
        var ruleKeys = existingRules.Select(rule => (rule.MatchKind, rule.Pattern)).ToHashSet();

        for (var index = 0; index < Products.Count; index++)
        {
            var seed = Products[index];
            var existing = existingProducts.FirstOrDefault(product =>
                string.Equals(product.Name, seed.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(product.Publisher, seed.Publisher, StringComparison.OrdinalIgnoreCase));
            var productId = existing?.Id ?? DeterministicId(ProductKind, index);
            productIds[seed.Key] = productId;
            if (existing is null)
            {
                dbContext.SoftwareProducts.Add(new SoftwareProduct
                {
                    Id = productId,
                    Name = seed.Name,
                    Publisher = seed.Publisher,
                    Category = seed.Category,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                productsAdded++;
            }

            // Canonicalised through the same helper the matcher uses, so a seeded pattern and a typed
            // one are the same string by the time either is compared.
            var pattern = SoftwareNormaliser.Canonicalise(seed.Pattern);
            if (ruleKeys.Add((seed.MatchKind, pattern)))
            {
                dbContext.SoftwareNormalisationRules.Add(new SoftwareNormalisationRule
                {
                    Id = DeterministicId(RuleKind, index),
                    ProductId = productId,
                    MatchKind = seed.MatchKind,
                    Pattern = pattern,
                    Priority = 0,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                rulesAdded++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var rules = Products
            .Select(seed => new SoftwareRule(
                productIds[seed.Key], seed.MatchKind, SoftwareNormaliser.Canonicalise(seed.Pattern), 0))
            .ToList();
        var installsAdded = 0;
        var seededCiIds = Installs.Select(install => ciIds.GetValueOrDefault(install.CiKey))
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        var existingInstalls = (await dbContext.InstalledSoftware
                .Where(install => seededCiIds.Contains(install.CiId))
                .Select(install => new { install.CiId, install.IdentityKey })
                .ToListAsync(cancellationToken))
            .Select(install => (install.CiId, install.IdentityKey))
            .ToHashSet();
        for (var index = 0; index < Installs.Count; index++)
        {
            var seed = Installs[index];

            // A CI the estate does not hold is skipped rather than fatal: this seeder is additive and a
            // database seeded before the estate grew should still get everything it can.
            if (!ciIds.TryGetValue(seed.CiKey, out var ciId))
            {
                continue;
            }

            // Guarded on the same (CI, identity) the unique index carries, so an inventory import that
            // reached this machine first is left alone rather than colliding with it.
            var identityKey = SoftwareNormaliser.IdentityKeyFor(seed.RawName, seed.Version);
            if (!existingInstalls.Add((ciId, identityKey)))
            {
                continue;
            }

            dbContext.InstalledSoftware.Add(new InstalledSoftware
            {
                Id = DeterministicId(InstallKind, index),
                CiId = ciId,
                IdentityKey = identityKey,
                RawName = seed.RawName,
                RawPublisher = seed.Publisher,
                Version = seed.Version,
                ProductId = SoftwareNormaliser.Match(seed.RawName, rules),
                InstalledOn = today.AddDays(-30 - index),
                Source = SeedSource,
                FirstSeenAt = now,
                LastSeenAt = now,
                SightingCount = 1,
            });
            installsAdded++;
        }

        var poolsAdded = 0;
        var seededProductIds = productIds.Values.ToArray();
        var existingPools = (await dbContext.LicensePools
                .Where(pool => seededProductIds.Contains(pool.ProductId))
                .Select(pool => new { pool.ProductId, pool.Name })
                .ToListAsync(cancellationToken))
            .Select(pool => (pool.ProductId, pool.Name))
            .ToHashSet();
        for (var index = 0; index < LicensePools.Count; index++)
        {
            var seed = LicensePools[index];
            if (!existingPools.Add((productIds[seed.ProductKey], seed.Name)))
            {
                continue;
            }

            dbContext.LicensePools.Add(new LicensePool
            {
                Id = DeterministicId(PoolKind, index),
                ProductId = productIds[seed.ProductKey],
                Name = seed.Name,
                Reference = seed.Reference,
                Entitlements = seed.Entitlements,
                PurchaseDate = today.AddDays(-400),
                ExpiresAt = seed.ExpiresInDays is { } days ? today.AddDays(days) : null,
                Notes = seed.Notes,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            poolsAdded++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(productsAdded, rulesAdded, installsAdded, poolsAdded);
    }

    /// <summary>
    /// The WP-2.8 scheme, continued: ids derive from position, so appending to a table is safe and
    /// reordering one is a renumbering. Kinds 1-8 belong to the estate seeder.
    /// </summary>
    private static Guid DeterministicId(int kind, int index) =>
        Guid.Parse($"01980002-{kind:0000}-7000-8000-{index:0000}{0:00000000}");
}
