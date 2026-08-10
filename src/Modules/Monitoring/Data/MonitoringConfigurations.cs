using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Monitoring.Data;

public sealed class MonitoredDeviceConfiguration : IEntityTypeConfiguration<MonitoredDevice>
{
    public void Configure(EntityTypeBuilder<MonitoredDevice> builder)
    {
        builder.ToTable("monitored_devices", "monitoring");
        builder.HasKey(device => device.Id);
        builder.Property(device => device.Address).HasMaxLength(255).IsRequired();
        builder.Property(device => device.PollerGroup).HasMaxLength(100).IsRequired();
        builder.Property(device => device.Notes).HasMaxLength(2_000);
        builder.Property(device => device.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(device => device.UpdatedBy).HasMaxLength(200).IsRequired();

        // A CI monitored twice would report two opinions about whether it is up, and every future
        // alert-to-CI correlation would have to pick one.
        builder.HasIndex(device => device.CiId).IsUnique();
        builder.HasIndex(device => new { device.PollerGroup, device.IsEnabled });
    }
}

public sealed class CheckDefinitionConfiguration : IEntityTypeConfiguration<CheckDefinition>
{
    public void Configure(EntityTypeBuilder<CheckDefinition> builder)
    {
        builder.ToTable("check_definitions", "monitoring");
        builder.HasKey(check => check.Id);
        builder.Property(check => check.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(check => check.Name).HasMaxLength(200).IsRequired();
        builder.Property(check => check.Comparison).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(check => check.ParametersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(check => check.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(check => check.UpdatedBy).HasMaxLength(200).IsRequired();

        // A check is meaningless without its device, so it goes when the device goes — unlike a CI
        // relationship, which is a fact about two peers and blocks the delete instead.
        builder.HasOne(check => check.Device).WithMany(device => device.Checks)
            .HasForeignKey(check => check.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Two checks of one type with one name on one device would be indistinguishable in an alert.
        builder.HasIndex(check => new { check.DeviceId, check.Name }).IsUnique();
    }
}

public sealed class MaintenanceWindowConfiguration : IEntityTypeConfiguration<MaintenanceWindow>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        builder.ToTable("maintenance_windows", "monitoring");
        builder.HasKey(window => window.Id);
        builder.Property(window => window.Name).HasMaxLength(200).IsRequired();
        builder.Property(window => window.Description).HasMaxLength(2_000);
        builder.Property(window => window.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(window => window.UpdatedBy).HasMaxLength(200).IsRequired();

        builder.HasIndex(window => new { window.StartsAt, window.EndsAt });
        builder.HasIndex(window => window.IsActive);
    }
}

public sealed class MaintenanceWindowDeviceConfiguration : IEntityTypeConfiguration<MaintenanceWindowDevice>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindowDevice> builder)
    {
        builder.ToTable("maintenance_window_devices", "monitoring");
        builder.HasKey(scope => new { scope.MaintenanceWindowId, scope.DeviceId });

        builder.HasOne(scope => scope.MaintenanceWindow).WithMany(window => window.Devices)
            .HasForeignKey(scope => scope.MaintenanceWindowId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting a device silently narrows the windows that named it; the window itself survives.
        builder.HasOne(scope => scope.Device).WithMany()
            .HasForeignKey(scope => scope.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PollerConfiguration : IEntityTypeConfiguration<Poller>
{
    public void Configure(EntityTypeBuilder<Poller> builder)
    {
        builder.ToTable("pollers", "monitoring");
        builder.HasKey(poller => poller.Id);
        builder.Property(poller => poller.Name).HasMaxLength(100).IsRequired();
        builder.Property(poller => poller.PollerGroup).HasMaxLength(100).IsRequired();
        builder.Property(poller => poller.AgentVersion).HasMaxLength(50);

        // The registration key: a restarted poller re-registers as itself.
        builder.HasIndex(poller => poller.Name).IsUnique();

        // The heartbeat evaluator's only query: enabled pollers that have spoken at least once.
        builder.HasIndex(poller => new { poller.IsEnabled, poller.LastHeartbeatAt });
    }
}

public sealed class MonitoringConfigChangeConfiguration : IEntityTypeConfiguration<MonitoringConfigChange>
{
    public void Configure(EntityTypeBuilder<MonitoringConfigChange> builder)
    {
        builder.ToTable("config_changes", "monitoring");

        // The version is allocated by the service under an advisory lock, never by the database, so
        // that it is assigned and committed in the same order.
        builder.HasKey(change => change.Version);
        builder.Property(change => change.Version).ValueGeneratedNever();
        builder.Property(change => change.EntityType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(change => change.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(change => change.PollerGroup).HasMaxLength(100);

        builder.HasIndex(change => change.DeviceId);
        builder.HasIndex(change => change.PollerGroup);
    }
}
