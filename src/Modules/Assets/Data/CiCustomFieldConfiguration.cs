using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class CiCustomFieldConfiguration : IEntityTypeConfiguration<CiCustomField>
{
    public void Configure(EntityTypeBuilder<CiCustomField> builder)
    {
        builder.ToTable("ci_custom_field_definitions", "assets");
        builder.HasKey(field => field.Id);
        builder.Property(field => field.CiType).HasConversion<string>().HasMaxLength(20);
        builder.Property(field => field.Key).HasMaxLength(50).IsRequired();
        builder.Property(field => field.Label).HasMaxLength(100).IsRequired();
        builder.Property(field => field.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(field => field.Options).HasColumnType("text[]").IsRequired();
        builder.HasIndex(field => new { field.CiType, field.Key }).IsUnique();
    }
}

public sealed class CiCustomFieldValueConfiguration : IEntityTypeConfiguration<CiCustomFieldValue>
{
    public void Configure(EntityTypeBuilder<CiCustomFieldValue> builder)
    {
        builder.ToTable("ci_custom_field_values", "assets");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Value).HasMaxLength(1_000).IsRequired();
        builder.HasOne(value => value.Ci).WithMany(ci => ci.CustomFieldValues)
            .HasForeignKey(value => value.CiId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(value => value.Field).WithMany()
            .HasForeignKey(value => value.FieldId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(value => new { value.CiId, value.FieldId }).IsUnique();
    }
}
