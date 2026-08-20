using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class ProductCatalogEntryConfiguration : IEntityTypeConfiguration<ProductCatalogEntry>
{
    public void Configure(EntityTypeBuilder<ProductCatalogEntry> builder)
    {
        builder.ToTable("product_catalog_entries", "assets");
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.ModelIdentifier).HasMaxLength(BarcodeLength).IsRequired();
        builder.Property(entry => entry.Manufacturer).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.Model).HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.ProductNumber).HasMaxLength(BarcodeLength);
        builder.Property(entry => entry.DeviceType).HasMaxLength(50);
        builder.Property(entry => entry.Source).HasConversion<string>().HasMaxLength(16);
        builder.Property(entry => entry.CreatedBy).HasMaxLength(200).IsRequired();
        // The reuse key has to resolve to one product, or the catalogue answers a question twice.
        builder.HasIndex(entry => entry.ModelIdentifier).IsUnique();
    }

    /// <summary>Matches <c>BarcodeParser.MaxLength</c> — nothing longer can be scanned in.</summary>
    private const int BarcodeLength = 128;
}
