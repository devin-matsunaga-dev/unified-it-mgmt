using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("sites", table =>
            table.HasCheckConstraint("ck_sites_code_not_empty", "length(code) > 0"));
        builder.HasKey(site => site.Id);
        builder.Property(site => site.Id).ValueGeneratedNever();
        builder.Property(site => site.Code).HasMaxLength(50).IsRequired();
        builder.Property(site => site.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(site => site.Code).IsUnique();
    }
}