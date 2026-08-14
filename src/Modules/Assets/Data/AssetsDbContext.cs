using Microsoft.EntityFrameworkCore;

namespace Modules.Assets.Data;

public sealed class AssetsDbContext(DbContextOptions<AssetsDbContext> options) : DbContext(options)
{
    public DbSet<ConfigurationItem> Cis => Set<ConfigurationItem>();
    public DbSet<HardwareCi> HardwareCis => Set<HardwareCi>();
    public DbSet<ServerCi> ServerCis => Set<ServerCi>();
    public DbSet<NetworkDeviceCi> NetworkDeviceCis => Set<NetworkDeviceCi>();
    public DbSet<SoftwareCi> SoftwareCis => Set<SoftwareCi>();
    public DbSet<VirtualCi> VirtualCis => Set<VirtualCi>();
    public DbSet<LogicalCi> LogicalCis => Set<LogicalCi>();
    public DbSet<CiCustomField> CiCustomFields => Set<CiCustomField>();
    public DbSet<CiCustomFieldValue> CiCustomFieldValues => Set<CiCustomFieldValue>();
    public DbSet<CiLifecycleTransition> CiLifecycleTransitions => Set<CiLifecycleTransition>();
    public DbSet<CiLifecycleHistory> CiLifecycleHistory => Set<CiLifecycleHistory>();
    public DbSet<CiAssignmentEntry> CiAssignments => Set<CiAssignmentEntry>();
    public DbSet<CiRelationship> CiRelationships => Set<CiRelationship>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractNotification> ContractNotifications => Set<ContractNotification>();
    public DbSet<DiscoveredDevice> DiscoveredDevices => Set<DiscoveredDevice>();
    public DbSet<CiDiscoveryFacts> CiDiscoveryFacts => Set<CiDiscoveryFacts>();
    public DbSet<PhysicalAuditSession> PhysicalAuditSessions => Set<PhysicalAuditSession>();
    public DbSet<PhysicalAuditScan> PhysicalAuditScans => Set<PhysicalAuditScan>();
    public DbSet<TopologyMap> TopologyMaps => Set<TopologyMap>();
    public DbSet<TopologyMapNode> TopologyMapNodes => Set<TopologyMapNode>();
    public DbSet<SoftwareProduct> SoftwareProducts => Set<SoftwareProduct>();
    public DbSet<SoftwareNormalisationRule> SoftwareNormalisationRules => Set<SoftwareNormalisationRule>();
    public DbSet<InstalledSoftware> InstalledSoftware => Set<InstalledSoftware>();
    public DbSet<LicensePool> LicensePools => Set<LicensePool>();

    /// <summary>Result shape of the recursive-CTE traversals; never mapped to a table.</summary>
    public DbSet<CiGraphHop> CiGraphHops => Set<CiGraphHop>();

    /// <summary>Result shape of the WP-5.1 correlation traversal; never mapped to a table.</summary>
    public DbSet<CiDependencyHop> CiDependencyHops => Set<CiDependencyHop>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("assets");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AssetsDbContext).Assembly);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Query-result shapes (CiGraphHop, CiDependencyHop) map to no table, so only rename the
            // ones that do.
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
