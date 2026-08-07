using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class ConfigurationItemConfiguration : IEntityTypeConfiguration<ConfigurationItem>
{
    public void Configure(EntityTypeBuilder<ConfigurationItem> builder)
    {
        builder.ToTable("cis", "assets");
        builder.HasKey(ci => ci.Id);
        builder.HasDiscriminator<CiType>("CiType")
            .HasValue<HardwareCi>(CiType.Hardware)
            .HasValue<ServerCi>(CiType.Server)
            .HasValue<NetworkDeviceCi>(CiType.NetworkDevice)
            .HasValue<SoftwareCi>(CiType.Software)
            .HasValue<VirtualCi>(CiType.Virtual)
            .HasValue<LogicalCi>(CiType.Logical);
        builder.Property<CiType>("CiType").HasConversion<string>().HasMaxLength(20);
        builder.Ignore(ci => ci.Type);

        builder.Property(ci => ci.Name).HasMaxLength(200).IsRequired();
        builder.Property(ci => ci.AssetTag).HasMaxLength(64);
        builder.Property(ci => ci.SerialNumber).HasMaxLength(128);
        builder.Property(ci => ci.Description).HasMaxLength(2_000);

        // Asset tag and serial are the WP-2.5 dedupe keys, so they must be unique where present —
        // filtered so the many CIs without either do not collide on NULL.
        builder.HasIndex(ci => ci.AssetTag).IsUnique().HasFilter("asset_tag IS NOT NULL");
        builder.HasIndex(ci => ci.SerialNumber).IsUnique().HasFilter("serial_number IS NOT NULL");
        builder.HasIndex("CiType", "Name");
        builder.HasIndex(ci => ci.IsActive);
    }
}

public sealed class HardwareCiConfiguration : IEntityTypeConfiguration<HardwareCi>
{
    public void Configure(EntityTypeBuilder<HardwareCi> builder)
    {
        builder.Property(ci => ci.Manufacturer).HasMaxLength(120);
        builder.Property(ci => ci.Model).HasMaxLength(120);
    }
}

// Hostname, RamGb and Vendor each appear on two sibling types. TPH would otherwise uniquify the
// second one into a "hostname1"-style column, so every colliding property names its column outright.
public sealed class ServerCiConfiguration : IEntityTypeConfiguration<ServerCi>
{
    public void Configure(EntityTypeBuilder<ServerCi> builder)
    {
        builder.Property(ci => ci.Hostname).HasColumnName("server_hostname").HasMaxLength(253);
        builder.Property(ci => ci.OperatingSystem).HasMaxLength(120);
        builder.Property(ci => ci.RamGb).HasColumnName("server_ram_gb");
    }
}

public sealed class NetworkDeviceCiConfiguration : IEntityTypeConfiguration<NetworkDeviceCi>
{
    public void Configure(EntityTypeBuilder<NetworkDeviceCi> builder)
    {
        builder.Property(ci => ci.ManagementIp).HasMaxLength(45);
        builder.Property(ci => ci.Vendor).HasColumnName("network_vendor").HasMaxLength(120);
    }
}

public sealed class SoftwareCiConfiguration : IEntityTypeConfiguration<SoftwareCi>
{
    public void Configure(EntityTypeBuilder<SoftwareCi> builder)
    {
        builder.Property(ci => ci.Vendor).HasColumnName("software_vendor").HasMaxLength(120);
        builder.Property(ci => ci.Version).HasMaxLength(64);
    }
}

public sealed class VirtualCiConfiguration : IEntityTypeConfiguration<VirtualCi>
{
    public void Configure(EntityTypeBuilder<VirtualCi> builder)
    {
        builder.Property(ci => ci.Hostname).HasColumnName("virtual_hostname").HasMaxLength(253);
        builder.Property(ci => ci.Hypervisor).HasMaxLength(120);
        builder.Property(ci => ci.RamGb).HasColumnName("virtual_ram_gb");
    }
}

public sealed class LogicalCiConfiguration : IEntityTypeConfiguration<LogicalCi>
{
    public void Configure(EntityTypeBuilder<LogicalCi> builder)
    {
        builder.Property(ci => ci.Purpose).HasMaxLength(500);
        builder.Property(ci => ci.ServiceTier).HasMaxLength(40);
    }
}
