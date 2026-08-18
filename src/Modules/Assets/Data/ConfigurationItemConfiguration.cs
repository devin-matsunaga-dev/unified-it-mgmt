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
        builder.Property(ci => ci.LifecycleState).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(ci => ci.OwnerName).HasMaxLength(200);
        builder.Property(ci => ci.DepartmentName).HasMaxLength(200);
        builder.Property(ci => ci.SiteName).HasMaxLength(200);

        // Asset tag and serial are the WP-2.5 dedupe keys, so they must be unique where present —
        // filtered so the many CIs without either do not collide on NULL.
        builder.HasIndex(ci => ci.AssetTag).IsUnique().HasFilter("asset_tag IS NOT NULL");
        builder.HasIndex(ci => ci.SerialNumber).IsUnique().HasFilter("serial_number IS NOT NULL");
        // Covered CIs are what a contract page lists, so a contract cannot be deleted while any CI
        // still names it; CiCoverageService turns the refusal into a 409.
        builder.HasOne(ci => ci.Contract).WithMany(contract => contract.Cis)
            .HasForeignKey(ci => ci.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        // WP-5.4. Written out as SQL rather than declared with HasGeneratedTsVectorColumn, which the rest of
        // the solution uses, for two reasons that both come from this table:
        //
        //  * TPH. A hostname and a management IP are exactly what somebody searches a server or a switch by,
        //    and they are properties of derived types — which the fluent helper cannot reach from the base
        //    type's configuration, even though TPH has already put all of them in this one table.
        //  * Weighting. A CI has a name and it has a paragraph about it; matching the name is a better
        //    answer, and setweight is what tells ts_rank so. Without it "core switch" ranks a CI whose
        //    description mentions the core switch level with the core switch itself.
        //
        // The expression must be IMMUTABLE for Postgres to accept it as a generated column, which is why the
        // dictionary is named explicitly: the one-argument to_tsvector reads a session setting and is only
        // STABLE. Nothing here is user input — this is a schema definition, not a query (ARCHITECTURE §7.5).
        builder.Property(ci => ci.SearchVector)
            .HasColumnType("tsvector")
            .HasComputedColumnSql(
                """
                setweight(to_tsvector('english', coalesce(name, '') || ' ' || coalesce(asset_tag, '')
                    || ' ' || coalesce(serial_number, '')), 'A')
                || setweight(to_tsvector('english', coalesce(server_hostname, '') || ' '
                    || coalesce(virtual_hostname, '') || ' ' || coalesce(management_ip, '')), 'B')
                || setweight(to_tsvector('english', coalesce(description, '')), 'C')
                """,
                stored: true);
        builder.HasIndex(ci => ci.SearchVector).HasMethod("GIN");

        builder.HasIndex("CiType", "Name");
        builder.HasIndex(ci => ci.ContractId);
        builder.HasIndex(ci => ci.WarrantyExpiresAt);
        builder.HasIndex(ci => ci.IsActive);
        builder.HasIndex(ci => ci.LifecycleState);
        builder.HasIndex(ci => ci.OwnerUserId);
        builder.HasIndex(ci => ci.DepartmentId);
        builder.HasIndex(ci => ci.SiteId);
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
        // Named rather than an enum column: the allowed values live in NetworkDeviceRoles and are
        // validated by CiTypeSchema, so adding a role later is not a database migration.
        builder.Property(ci => ci.Role).HasColumnName("network_role").HasMaxLength(32);
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
