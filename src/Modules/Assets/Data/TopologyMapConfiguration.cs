using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class TopologyMapConfiguration : IEntityTypeConfiguration<TopologyMap>
{
    public void Configure(EntityTypeBuilder<TopologyMap> builder)
    {
        builder.ToTable("topology_maps", "assets");
        builder.HasKey(map => map.Id);

        builder.Property(map => map.Name).HasMaxLength(200).IsRequired();
        builder.Property(map => map.Description).HasMaxLength(1_000);
        builder.Property(map => map.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(map => map.UpdatedBy).HasMaxLength(200);

        builder.HasIndex(map => map.Name).IsUnique();

        // The pins are the map. Deleting the map takes them with it — unlike a relationship, nothing
        // here is a fact about the estate that outlives the drawing it was made for.
        builder.HasMany(map => map.Nodes).WithOne(node => node.Map)
            .HasForeignKey(node => node.TopologyMapId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TopologyMapNodeConfiguration : IEntityTypeConfiguration<TopologyMapNode>
{
    public void Configure(EntityTypeBuilder<TopologyMapNode> builder)
    {
        builder.ToTable("topology_map_nodes", "assets");
        builder.HasKey(node => node.Id);

        // One pin per CI per map: a CI cannot be in two places on one drawing.
        builder.HasIndex(node => new { node.TopologyMapId, node.CiId }).IsUnique();

        // A pin is a position for a CI, so a CI that leaves the estate takes its pins with it. That is
        // the CiDiscoveryFacts call rather than the CiRelationship one: nothing anybody asserted is
        // lost, and refusing to delete a decommissioned switch because somebody once drew it would make
        // the map an obstacle to keeping the CMDB true.
        builder.HasOne(node => node.Ci).WithMany()
            .HasForeignKey(node => node.CiId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
