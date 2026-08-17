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

        // WP-5.4. Unweighted, unlike tickets and CIs: a device has no title and no prose, only three short
        // facts about itself, and pretending one of them outranks the others would be an invention.
        builder.HasGeneratedTsVectorColumn(
                device => device.SearchVector,
                "english",
                device => new { device.Address, device.PollerGroup, device.Notes })
            .HasIndex(device => device.SearchVector).HasMethod("GIN");
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

        // Filtered, because most checks authenticate to nothing. It serves two reads: the vault's
        // delete guard counting the checks that name one credential, and the poller's credential
        // scope collecting the distinct ids a group needs.
        builder.HasIndex(check => check.CredentialId)
            .HasFilter("credential_id is not null");
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

        // WP-5.1. Both readers of this column ask the same question — "what is filed under this alert"
        // — so it is indexed rather than scanned: the root-cause ticket asks once per raise, and the
        // alert board asks once per root-cause row it renders.
        builder.HasIndex(alert => alert.RootCauseAlertId)
            .HasFilter("root_cause_alert_id IS NOT NULL");

        // WP-5.4. The summary is the sentence a person remembers ("CPU above 90%"); the rule id and the
        // metric name are the strings they copy out of a ticket or a chart and paste into a search box.
        builder.HasGeneratedTsVectorColumn(
                alert => alert.SearchVector,
                "english",
                alert => new { alert.Summary, alert.RuleId, alert.MetricName })
            .HasIndex(alert => alert.SearchVector).HasMethod("GIN");

        // SetNull rather than Cascade or Restrict, and the choice is forced by the cascade below.
        // Alerts go when their device goes, so deleting the switch deletes the cause while its
        // consequences live on other devices and survive: Cascade would delete their alerts too — an
        // outage disappearing from the board because somebody decommissioned the thing that caused it
        // — and Restrict would make deleting that device fail with a foreign key error naming a table
        // the operator never touched. Nulling it is self-healing: the consequence is left suppressed
        // under nothing, its next reading finds no failing dependency, and it publishes on its own
        // account one cycle later.
        builder.HasOne<Alert>().WithMany()
            .HasForeignKey(alert => alert.RootCauseAlertId)
            .OnDelete(DeleteBehavior.SetNull);

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

        // One window per approved change, however many times the approval is delivered (WP-5.8). The
        // filtered form is how "one X per Y" is made true here for the fourth time — WP-3.6's alert
        // ticket, WP-5.6's runbook execution, WP-5.7's suggestion — and it has to be filtered because
        // most windows are typed in by an operator and have no change behind them at all.
        builder.HasIndex(window => window.ChangeRequestId)
            .IsUnique()
            .HasFilter("change_request_id IS NOT NULL");
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

public sealed class DeviceInterfaceConfiguration : IEntityTypeConfiguration<DeviceInterface>
{
    public void Configure(EntityTypeBuilder<DeviceInterface> builder)
    {
        builder.ToTable("device_interfaces", "monitoring");

        // The device's own index, not a surrogate: an interface has no identity apart from the
        // device that has it, and a generated id would let one poll insert a second row for a port
        // the previous poll already recorded.
        builder.HasKey(link => new { link.DeviceId, link.IfIndex });
        builder.Property(link => link.Name).HasMaxLength(100);
        builder.Property(link => link.Alias).HasMaxLength(200);
        builder.Property(link => link.MacAddress).HasMaxLength(100);
        builder.Property(link => link.AdminStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(link => link.OperStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne(link => link.Device).WithMany()
            .HasForeignKey(link => link.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // No further index: the interface table's own read is one device's ports in index order,
        // which the primary key already is.
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

public sealed class ScanProfileConfiguration : IEntityTypeConfiguration<ScanProfile>
{
    public void Configure(EntityTypeBuilder<ScanProfile> builder)
    {
        builder.ToTable("scan_profiles", "monitoring");
        builder.HasKey(profile => profile.Id);
        builder.Property(profile => profile.Name).HasMaxLength(200).IsRequired();
        builder.Property(profile => profile.Description).HasMaxLength(2_000);
        builder.Property(profile => profile.DiscoveryGroup).HasMaxLength(100).IsRequired();
        builder.Property(profile => profile.RangesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(profile => profile.PortsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(profile => profile.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(profile => profile.UpdatedBy).HasMaxLength(200).IsRequired();

        // Two profiles with one name are indistinguishable in the log line that says which scan found
        // a device, which is the only place most people will ever read a profile's name.
        builder.HasIndex(profile => profile.Name).IsUnique();

        // The scanner's only query: what this group has to run.
        builder.HasIndex(profile => new { profile.DiscoveryGroup, profile.IsEnabled });
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

public sealed class RunbookConfiguration : IEntityTypeConfiguration<Runbook>
{
    public void Configure(EntityTypeBuilder<Runbook> builder)
    {
        builder.ToTable("runbooks", "monitoring");
        builder.HasKey(runbook => runbook.Id);
        builder.Property(runbook => runbook.Key).HasMaxLength(100).IsRequired();
        builder.Property(runbook => runbook.Name).HasMaxLength(200).IsRequired();
        builder.Property(runbook => runbook.Description).HasMaxLength(2_000);
        builder.Property(runbook => runbook.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(runbook => runbook.UpdatedBy).HasMaxLength(200).IsRequired();

        // One registration per catalogue key. Two rows for `restart-service` would be two rate limits
        // over the same allowlisted action, which is a bound anybody could walk around by registering
        // the same thing twice.
        builder.HasIndex(runbook => runbook.Key).IsUnique();
    }
}

public sealed class RunbookTriggerConfiguration : IEntityTypeConfiguration<RunbookTrigger>
{
    public void Configure(EntityTypeBuilder<RunbookTrigger> builder)
    {
        builder.ToTable("runbook_triggers", "monitoring");
        builder.HasKey(trigger => trigger.Id);
        builder.Property(trigger => trigger.MetricName).HasMaxLength(100).IsRequired();
        builder.Property(trigger => trigger.MinimumSeverity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(trigger => trigger.ParametersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(trigger => trigger.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(trigger => trigger.UpdatedBy).HasMaxLength(200).IsRequired();

        builder.HasOne(trigger => trigger.Runbook)
            .WithMany(runbook => runbook.Triggers)
            .HasForeignKey(trigger => trigger.RunbookId)
            .OnDelete(DeleteBehavior.Cascade);

        // The consumer's only query: which triggers a raised alert matches.
        builder.HasIndex(trigger => new { trigger.MetricName, trigger.IsEnabled });
    }
}

public sealed class RunbookExecutionConfiguration : IEntityTypeConfiguration<RunbookExecution>
{
    public void Configure(EntityTypeBuilder<RunbookExecution> builder)
    {
        builder.ToTable("runbook_executions", "monitoring");
        builder.HasKey(execution => execution.Id);
        builder.Property(execution => execution.RunbookKey).HasMaxLength(100).IsRequired();
        builder.Property(execution => execution.RuleId).HasMaxLength(200);
        builder.Property(execution => execution.ParametersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(execution => execution.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(execution => execution.RequestedBy).HasMaxLength(200).IsRequired();
        builder.Property(execution => execution.PollerName).HasMaxLength(100);
        builder.Property(execution => execution.Output).HasMaxLength(16_000);
        builder.Property(execution => execution.Error).HasMaxLength(4_000);

        // Restrict, not cascade: deleting a runbook must not silently delete the record of everything
        // it ever did on the estate. The service refuses the delete while executions exist and says so.
        builder.HasOne(execution => execution.Runbook)
            .WithMany()
            .HasForeignKey(execution => execution.RunbookId)
            .OnDelete(DeleteBehavior.Restrict);

        // "One execution per alert per runbook", as a constraint rather than a hope about ordering —
        // the same call WP-3.6 made for its dedupe row. A redelivered AlertRaised, or an escalation
        // carrying the same alert id, loses this insert and runs nothing.
        builder.HasIndex(execution => new { execution.RunbookId, execution.AlertId })
            .IsUnique()
            .HasFilter("alert_id IS NOT NULL");

        // What the poller claims, and what the sweeper looks for.
        builder.HasIndex(execution => new { execution.Status, execution.RequestedAt });
        builder.HasIndex(execution => execution.DeviceId);
    }
}
