using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class CiRelationshipConfiguration : IEntityTypeConfiguration<CiRelationship>
{
    public void Configure(EntityTypeBuilder<CiRelationship> builder)
    {
        builder.ToTable("ci_relationships", "assets");
        builder.HasKey(relationship => relationship.Id);
        builder.Property(relationship => relationship.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(relationship => relationship.Description).HasMaxLength(500);
        builder.Property(relationship => relationship.CreatedBy).HasMaxLength(200).IsRequired();

        // Deleting a CI out from under its edges would orphan the graph silently, so the database
        // refuses it and CiService turns that into a 409 the caller can act on.
        builder.HasOne(relationship => relationship.SourceCi).WithMany()
            .HasForeignKey(relationship => relationship.SourceCiId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(relationship => relationship.TargetCi).WithMany()
            .HasForeignKey(relationship => relationship.TargetCiId)
            .OnDelete(DeleteBehavior.Restrict);

        // One edge per (source, target, type): the same pair may be related two ways (a VM both runs
        // on and depends on its host), but not twice the same way.
        builder.HasIndex(relationship => new
            {
                relationship.SourceCiId,
                relationship.TargetCiId,
                relationship.Type,
            })
            .IsUnique();

        // The traversal joins on one end or the other depending on the direction it walks.
        builder.HasIndex(relationship => relationship.SourceCiId);
        builder.HasIndex(relationship => relationship.TargetCiId);
    }
}

public sealed class CiGraphHopConfiguration : IEntityTypeConfiguration<CiGraphHop>
{
    /// <summary>Unmapped from any table: rows only ever arrive from a <c>FromSql</c> traversal.</summary>
    public void Configure(EntityTypeBuilder<CiGraphHop> builder)
    {
        builder.HasNoKey().ToTable((string?)null);
        builder.Property(hop => hop.CiId).HasColumnName("ci_id");
        builder.Property(hop => hop.Depth).HasColumnName("depth");
    }
}

public sealed class CiDependencyHopConfiguration : IEntityTypeConfiguration<CiDependencyHop>
{
    /// <summary>Unmapped from any table: rows only ever arrive from a <c>FromSql</c> traversal.</summary>
    public void Configure(EntityTypeBuilder<CiDependencyHop> builder)
    {
        builder.HasNoKey().ToTable((string?)null);
        builder.Property(hop => hop.CiId).HasColumnName("ci_id");
        builder.Property(hop => hop.DependsOnCiId).HasColumnName("depends_on_ci_id");
        builder.Property(hop => hop.Depth).HasColumnName("depth");
    }
}
