using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class DiscoveredDeviceConfiguration : IEntityTypeConfiguration<DiscoveredDevice>
{
    public void Configure(EntityTypeBuilder<DiscoveredDevice> builder)
    {
        builder.ToTable("discovered_devices", "assets");
        builder.HasKey(device => device.Id);

        builder.Property(device => device.IdentityKey).HasMaxLength(300).IsRequired();
        builder.Property(device => device.Address).HasMaxLength(45).IsRequired();
        builder.Property(device => device.Hostname).HasMaxLength(255);
        builder.Property(device => device.HostnameSource).HasMaxLength(20);
        builder.Property(device => device.OpenPortsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(device => device.SysName).HasMaxLength(255);
        builder.Property(device => device.SysDescription).HasMaxLength(2_000);
        builder.Property(device => device.SysObjectId).HasMaxLength(255);
        builder.Property(device => device.SysLocation).HasMaxLength(255);
        builder.Property(device => device.SysContact).HasMaxLength(255);
        builder.Property(device => device.NeighboursJson).HasColumnType("jsonb").IsRequired();
        builder.Property(device => device.ContenderCiIdsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(device => device.DiscoveryName).HasMaxLength(100).IsRequired();
        builder.Property(device => device.ScanProfileName).HasMaxLength(200).IsRequired();
        builder.Property(device => device.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(device => device.MatchRule).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(device => device.ReviewedBy).HasMaxLength(200);
        builder.Property(device => device.ReviewNote).HasMaxLength(2_000);

        // The identity is the row. Two rows for one device would put the same stranger on the review
        // card twice and let one of them be rejected while the other kept coming back.
        builder.HasIndex(device => device.IdentityKey).IsUnique();

        // The three lookups the intake performs on every single message it consumes, in the order it
        // performs them. Without these a busy sweep is a sequential scan of the ledger per address.
        builder.HasIndex(device => device.SysName);
        builder.HasIndex(device => device.Address);
        builder.HasIndex(device => device.Hostname);

        // The review queue's own query, and the CI page's.
        builder.HasIndex(device => new { device.Status, device.LastSeenAt });
        builder.HasIndex(device => device.CiId);
    }
}

public sealed class CiDiscoveryFactsConfiguration : IEntityTypeConfiguration<CiDiscoveryFacts>
{
    public void Configure(EntityTypeBuilder<CiDiscoveryFacts> builder)
    {
        builder.ToTable("ci_discovery_facts", "assets");

        // One current observation per CI, so the CI id is the key rather than a surrogate beside it.
        builder.HasKey(facts => facts.CiId);
        builder.Property(facts => facts.CiId).ValueGeneratedNever();

        builder.Property(facts => facts.Address).HasMaxLength(45).IsRequired();
        builder.Property(facts => facts.Hostname).HasMaxLength(255);
        builder.Property(facts => facts.OpenPortsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(facts => facts.SysName).HasMaxLength(255);
        builder.Property(facts => facts.SysDescription).HasMaxLength(2_000);
        builder.Property(facts => facts.SysObjectId).HasMaxLength(255);
        builder.Property(facts => facts.SysLocation).HasMaxLength(255);
        builder.Property(facts => facts.SysContact).HasMaxLength(255);
        builder.Property(facts => facts.NeighboursJson).HasColumnType("jsonb").IsRequired();
        builder.Property(facts => facts.DiscoveryName).HasMaxLength(100).IsRequired();
        builder.Property(facts => facts.ScanProfileName).HasMaxLength(200).IsRequired();

        // These are an observation of a CI, not a fact about the estate: when the CI goes, they go.
        // That is the opposite of the Restrict guards on relationships and ticket links, and rightly
        // so — nothing is lost that anybody asserted, and the next scan writes them again.
        builder.HasOne(facts => facts.Ci).WithOne()
            .HasForeignKey<CiDiscoveryFacts>(facts => facts.CiId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
