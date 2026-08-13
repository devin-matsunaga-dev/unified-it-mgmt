using Microsoft.EntityFrameworkCore;

namespace Modules.Monitoring.Data;

public sealed class MonitoringDbContext(DbContextOptions<MonitoringDbContext> options) : DbContext(options)
{
    public DbSet<MonitoredDevice> MonitoredDevices => Set<MonitoredDevice>();
    public DbSet<CheckDefinition> CheckDefinitions => Set<CheckDefinition>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<MaintenanceWindowDevice> MaintenanceWindowDevices => Set<MaintenanceWindowDevice>();
    public DbSet<Poller> Pollers => Set<Poller>();
    public DbSet<MonitoringConfigChange> ConfigChanges => Set<MonitoringConfigChange>();
    public DbSet<DeviceMetric> DeviceMetrics => Set<DeviceMetric>();
    public DbSet<DeviceInventoryFact> DeviceInventoryFacts => Set<DeviceInventoryFact>();
    public DbSet<DeviceInterface> DeviceInterfaces => Set<DeviceInterface>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<ScanProfile> ScanProfiles => Set<ScanProfile>();

    /// <summary>Result shape of a bucketed metric query; never mapped to a table.</summary>
    public DbSet<MetricBucket> MetricBuckets => Set<MetricBucket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("monitoring");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MonitoringDbContext).Assembly);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Query-result shapes (MetricBucket) map to no table, so only rename the ones that do.
            if (entity.GetTableName() is { } tableName)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName()!));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()!));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        if (name.Contains('_', StringComparison.Ordinal))
        {
            return name.ToLowerInvariant();
        }

        var result = new System.Text.StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0 && name[index - 1] != '_')
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}
