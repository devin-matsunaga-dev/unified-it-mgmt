using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class DashboardViewConfiguration : IEntityTypeConfiguration<DashboardView>
{
    public void Configure(EntityTypeBuilder<DashboardView> builder)
    {
        builder.ToTable("dashboard_views");
        builder.HasKey(view => view.Id);
        builder.Property(view => view.Id).ValueGeneratedNever();
        builder.Property(view => view.OwnerId).HasMaxLength(200).IsRequired();
        builder.Property(view => view.Name).HasMaxLength(60).IsRequired();
        builder.Property(view => view.PlacementsJson).HasColumnType("jsonb").IsRequired();

        // Unique per owner, because the tabs are read by name and two called "Mine" is a tab bar nobody can
        // navigate. Not unique globally: two people naming a view "Overview" is the normal case.
        builder.HasIndex(view => new { view.OwnerId, view.Name }).IsUnique();
        builder.HasIndex(view => new { view.OwnerId, view.IsActive });
    }
}
