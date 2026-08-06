using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Data.Migrations;

[DbContext(typeof(PlatformDbContext))]
public partial class PlatformDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasDefaultSchema("platform")
            .HasAnnotation("ProductVersion", "10.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("Platform.Data.AuditEntry", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("id");
            entity.Property<string>("Action").IsRequired().HasMaxLength(100)
                .HasColumnType("character varying(100)").HasColumnName("action");
            entity.Property<string>("ActorId").IsRequired().HasMaxLength(200)
                .HasColumnType("character varying(200)").HasColumnName("actor_id");
            entity.Property<string>("AfterJson").HasColumnType("jsonb").HasColumnName("after_json");
            entity.Property<string>("BeforeJson").HasColumnType("jsonb").HasColumnName("before_json");
            entity.Property<string>("CorrelationId").IsRequired().HasMaxLength(100)
                .HasColumnType("character varying(100)").HasColumnName("correlation_id");
            entity.Property<string>("EntityId").IsRequired().HasMaxLength(200)
                .HasColumnType("character varying(200)").HasColumnName("entity_id");
            entity.Property<string>("EntityType").IsRequired().HasMaxLength(200)
                .HasColumnType("character varying(200)").HasColumnName("entity_type");
            entity.Property<DateTimeOffset>("OccurredAt").HasColumnType("timestamp with time zone")
                .HasColumnName("occurred_at");
            entity.HasKey("Id");
            entity.HasIndex("OccurredAt").HasDatabaseName("ix_audit_entries_occurred_at");
            entity.HasIndex("EntityType", "EntityId")
                .HasDatabaseName("ix_audit_entries_entity_type_entity_id");
            entity.ToTable("audit_entries", "platform", table =>
                table.HasCheckConstraint(
                    "ck_audit_entries_actor_id_not_empty",
                    "length(actor_id) > 0"));
        });
    }
}
