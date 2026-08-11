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

public sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.ToTable("alerts", "monitoring");
        builder.HasKey(alert => alert.Id);
        builder.Property(alert => alert.RuleId).HasMaxLength(200).IsRequired();
        builder.Property(alert => alert.MetricName).HasMaxLength(200).IsRequired();
        builder.Property(alert => alert.Summary).HasMaxLength(1_000).IsRequired();
        builder.Property(alert => alert.PollerName).HasMaxLength(100).IsRequired();
        builder.Property(alert => alert.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(alert => alert.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(alert => alert.Suppression).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(alert => alert.AcknowledgedBy).HasMaxLength(200);
        builder.Property(alert => alert.AcknowledgedByName).HasMaxLength(200);

        // "Raised exactly once" as a database constraint rather than only as a state machine
        // invariant. Two consumers racing on one rule cannot both open an alert; the loser's
        // transaction fails and its message is retried against the state the winner left.
        builder.HasIndex(alert => new { alert.DeviceId, alert.RuleId })
            .IsUnique()
            .HasFilter("status = 'Open'");

        // The alert board's query: what is wrong now, worst first.
        builder.HasIndex(alert => new { alert.Status, alert.Severity, alert.RaisedAt });

        // An alert is about one device's rule, so it goes when the device goes — the same reasoning
        // as a check definition, and unlike a metric, which is a fact about a moment.
        builder.HasOne(alert => alert.Device).WithMany()
            .HasForeignKey(alert => alert.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
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

public sealed class DeviceMetricConfiguration : IEntityTypeConfiguration<DeviceMetric>
{
    public void Configure(EntityTypeBuilder<DeviceMetric> builder)
    {
        builder.ToTable("device_metrics", "monitoring");

        // Natural key, time first: a hypertable refuses a unique index that does not carry its
        // partitioning column, and this ordering is also the one a series query reads.
        builder.HasKey(metric => new { metric.Time, metric.DeviceId, metric.CheckId, metric.MetricName });
        // text rather than varchar(n), against the convention the rest of the schema follows:
        // create_hypertable warns about every varchar column it is handed, because a length-limited
        // type compresses worse in a chunk. Lengths are enforced by the ingestion service instead.
        builder.Property(metric => metric.MetricName).HasColumnType("text").IsRequired();
        builder.Property(metric => metric.Unit).HasColumnType("text");
        builder.Property(metric => metric.PollerName).HasColumnType("text").IsRequired();

        // "This device's CPU over the last day" — device, then metric, then time descending.
        builder.HasIndex(metric => new { metric.DeviceId, metric.MetricName, metric.Time })
            .IsDescending(false, false, true);

        // Deliberately no foreign key to monitored_devices. Timescale drops chunks out from under
        // the table on a retention pass, and a reading is a fact about a moment that stays true
        // after the device row is deleted.
    }
}

public sealed class DeviceInventoryFactConfiguration : IEntityTypeConfiguration<DeviceInventoryFact>
{
    public void Configure(EntityTypeBuilder<DeviceInventoryFact> builder)
    {
        builder.ToTable("device_inventory_facts", "monitoring");
        builder.HasKey(fact => new { fact.DeviceId, fact.Name });
        builder.Property(fact => fact.Name).HasMaxLength(100).IsRequired();
        builder.Property(fact => fact.Value).HasMaxLength(1_000).IsRequired();

        // Unlike a metric, this is current state rather than history, so it goes with its device.
        builder.HasOne(fact => fact.Device).WithMany()
            .HasForeignKey(fact => fact.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MetricBucketConfiguration : IEntityTypeConfiguration<MetricBucket>
{
    /// <summary>Unmapped from any table: rows only ever arrive from a <c>FromSql</c> aggregation.</summary>
    public void Configure(EntityTypeBuilder<MetricBucket> builder)
    {
        builder.HasNoKey().ToTable((string?)null);
        builder.Property(bucket => bucket.Bucket).HasColumnName("bucket");
        builder.Property(bucket => bucket.AvgValue).HasColumnName("avg_value");
        builder.Property(bucket => bucket.MinValue).HasColumnName("min_value");
        builder.Property(bucket => bucket.MaxValue).HasColumnName("max_value");
        builder.Property(bucket => bucket.SampleCount).HasColumnName("sample_count");
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
