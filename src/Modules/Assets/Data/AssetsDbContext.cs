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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("assets");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AssetsDbContext).Assembly);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnakeCase(entity.GetTableName()!));
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
