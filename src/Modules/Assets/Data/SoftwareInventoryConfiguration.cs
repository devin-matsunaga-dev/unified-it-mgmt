using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class SoftwareProductConfiguration : IEntityTypeConfiguration<SoftwareProduct>
{
    public void Configure(EntityTypeBuilder<SoftwareProduct> builder)
    {
        builder.ToTable("software_products", "assets");
        builder.HasKey(product => product.Id);
        builder.Property(product => product.Name).HasMaxLength(200).IsRequired();
        builder.Property(product => product.Publisher).HasMaxLength(200).IsRequired();
        builder.Property(product => product.Category).HasMaxLength(100);
        builder.Property(product => product.Notes).HasMaxLength(2_000);

        // One publisher's product name is the natural key, following the vendor precedent: two rows for
        // one product would split its install count in half and make every compliance figure wrong. The
        // service compares case-insensitively before the index ever sees it.
        builder.HasIndex(product => new { product.Publisher, product.Name }).IsUnique();
        builder.HasIndex(product => product.IsActive);
    }
}

public sealed class SoftwareNormalisationRuleConfiguration : IEntityTypeConfiguration<SoftwareNormalisationRule>
{
    public void Configure(EntityTypeBuilder<SoftwareNormalisationRule> builder)
    {
        builder.ToTable("software_normalisation_rules", "assets");
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.MatchKind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(rule => rule.Pattern).HasMaxLength(300).IsRequired();

        builder.HasOne(rule => rule.Product).WithMany(product => product.Rules)
            .HasForeignKey(rule => rule.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // The same pattern compared the same way cannot mean two products; that is a contradiction in
        // the catalogue rather than a priority question, and the service turns it into a 409.
        builder.HasIndex(rule => new { rule.MatchKind, rule.Pattern }).IsUnique();
        builder.HasIndex(rule => rule.ProductId);
    }
}

public sealed class InstalledSoftwareConfiguration : IEntityTypeConfiguration<InstalledSoftware>
{
    public void Configure(EntityTypeBuilder<InstalledSoftware> builder)
    {
        builder.ToTable("installed_software", "assets");
        builder.HasKey(install => install.Id);
        builder.Property(install => install.IdentityKey).HasMaxLength(500).IsRequired();
        builder.Property(install => install.RawName).HasMaxLength(300).IsRequired();
        builder.Property(install => install.RawPublisher).HasMaxLength(200);
        builder.Property(install => install.Version).HasMaxLength(100);
        builder.Property(install => install.Source).HasMaxLength(200).IsRequired();

        // An install is a property of one CI rather than a fact about two peers, so it goes when the CI
        // goes — the same rule WP-3.1 applied to a device's checks, and the opposite of the Restrict
        // guards on relationships and ticket links.
        builder.HasOne(install => install.Ci).WithMany()
            .HasForeignKey(install => install.CiId)
            .OnDelete(DeleteBehavior.Cascade);

        // A product with installs behind it is not deletable; deactivation is the way out, following
        // WP-1.9's ticket categories. Deleting it would silently un-normalise the history instead.
        builder.HasOne(install => install.Product).WithMany(product => product.Installs)
            .HasForeignKey(install => install.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per piece of software per machine: a re-import of the same file refreshes what is
        // already there rather than doubling every install count.
        builder.HasIndex(install => new { install.CiId, install.IdentityKey }).IsUnique();

        // The compliance rollup's own query: installs grouped by product.
        builder.HasIndex(install => install.ProductId);
        builder.HasIndex(install => install.RawName);
    }
}

public sealed class LicensePoolConfiguration : IEntityTypeConfiguration<LicensePool>
{
    public void Configure(EntityTypeBuilder<LicensePool> builder)
    {
        builder.ToTable("license_pools", "assets");
        builder.HasKey(pool => pool.Id);
        builder.Property(pool => pool.Name).HasMaxLength(200).IsRequired();
        builder.Property(pool => pool.Reference).HasMaxLength(100);
        builder.Property(pool => pool.Notes).HasMaxLength(2_000);

        builder.HasOne(pool => pool.Product).WithMany(product => product.LicensePools)
            .HasForeignKey(pool => pool.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(pool => new { pool.ProductId, pool.Name }).IsUnique();
        builder.HasIndex(pool => pool.ExpiresAt);
    }
}
