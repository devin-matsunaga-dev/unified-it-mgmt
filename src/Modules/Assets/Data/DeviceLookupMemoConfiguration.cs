using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class DeviceLookupMemoConfiguration : IEntityTypeConfiguration<DeviceLookupMemo>
{
    public void Configure(EntityTypeBuilder<DeviceLookupMemo> builder)
    {
        builder.ToTable("device_lookup_memos", "assets");
        builder.HasKey(memo => memo.Id);
        builder.Property(memo => memo.Identifier).HasMaxLength(128).IsRequired();
        builder.Property(memo => memo.Manufacturer).HasMaxLength(100);
        builder.Property(memo => memo.Model).HasMaxLength(200);
        builder.Property(memo => memo.ProductNumber).HasMaxLength(128);
        builder.Property(memo => memo.DeviceType).HasMaxLength(50);
        builder.Property(memo => memo.Source).HasMaxLength(32).IsRequired();
        // One device, one answer — and the lookup is an exact match on this alone.
        builder.HasIndex(memo => memo.Identifier).IsUnique();
    }
}
